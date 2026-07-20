# RSI : retour à la moyenne — SCALPING 1 m, multi-TF, long/short — porté sur NQ
# (miroir de rsi_retour_moyenne.py du frère crypto, re-calibrage du 2026-07-17 +
# correctif cadence 3 m / seuils 30-70 : la version 5 m à 25/75 sous-tradait).
# Contre-tendance FILTRÉE : on ne fade QUE dans le sens du régime
#   - régime haussier + survente  -> LONG  (acheter le repli d'une tendance haussière)
#   - régime baissier + surachat  -> SHORT (vendre le rebond d'une tendance baissière)
# Leviers mean-reversion identiques au frère :
#   - RSI sur barres 3 m (signal lu aux bornes de 3 min) ; stop/take en 1 m.
#   - COOLDOWN 45 min après sortie ; TAKE 0,8 % / STOP 1,0 % (les trades respirent).
# Sortie = retour à la moyenne (RSI ~50) OU take OU stop. Long ET short.
# Ce qui change : l'instrument (nq_instrument) et le sizing en contrats entiers (±1).
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL

PERIODE_RSI = 9          # RSI court (sur barres 3 m ≈ 27 min)
SURVENTE = 30            # RSI < 30 = survente (desserré : 25/75 sous-tradait)
SURACHAT = 70            # RSI > 70 = surachat
MOYENNE = 50             # retour à la moyenne = sortie
TF_SIGNAL = 3            # cadence du signal (minutes) : RSI lu sur barres 3 m
REGIME_N = 50            # SMA de régime sur barres 3 m (≈ 2,5 h)
STOP_PCT = 0.010         # stop 1,0 %
TAKE_PCT = 0.008         # cible 0,8 %
COOLDOWN_MIN = 45        # pas de nouvelle entrée dans les 45 min après une sortie
CONTRATS = 1             # granularité futures : 1 contrat NQ


class RsiRetourMoyenneNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        self.rsi = RelativeStrengthIndex(PERIODE_RSI, MovingAverageType.WILDERS)
        self.rsi_prec = None
        self.sma_regime = SimpleMovingAverage(REGIME_N)
        self.dernier_close_sig = None

        self.prix_entree = None
        self.temps_sortie = None
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.premier_close = None
        self.dernier_close = None

    def _regime(self):
        if not self.sma_regime.is_ready or self.dernier_close_sig is None:
            return 0
        return 1 if self.dernier_close_sig > self.sma_regime.current.value else -1

    def _cooldown_ok(self, maintenant):
        return (self.temps_sortie is None
                or (maintenant - self.temps_sortie).total_seconds() >= COOLDOWN_MIN * 60)

    def on_data(self, data: Slice):
        if self.nq not in data:
            return
        bar = data[self.nq]
        close = float(bar.value)
        if self.premier_close is None:
            self.premier_close = close
        self.dernier_close = close
        t = bar.end_time
        bas, haut = float(bar["low"]), float(bar["high"])
        pos = self.portfolio[self.nq]

        # 1) EN POSITION : stop/take vérifiés CHAQUE minute (extrême intra-barre).
        if pos.invested and self.prix_entree is not None:
            e = self.prix_entree
            if pos.is_long and (bas <= e * (1 - STOP_PCT) or haut >= e * (1 + TAKE_PCT)):
                self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t
            elif pos.is_short and (haut >= e * (1 + STOP_PCT) or bas <= e * (1 - TAKE_PCT)):
                self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t

        # 2) SIGNAL : RSI + régime aux bornes de 3 min (retour-moyenne + entrées).
        if t.minute % TF_SIGNAL != 0:
            return
        self.dernier_close_sig = close
        self.sma_regime.update(t, close)
        self.rsi.update(t, close)
        if not self.rsi.is_ready:
            return
        rsi = float(self.rsi.current.value)
        pos = self.portfolio[self.nq]            # relire (stop/take a pu liquider)

        if pos.invested and self.prix_entree is not None:
            # sortie sur retour à la moyenne (RSI ~50)
            if pos.is_long and rsi >= MOYENNE:
                self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t
            elif pos.is_short and rsi <= MOYENNE:
                self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t
        elif self.rsi_prec is not None and self._cooldown_ok(t):
            # à plat : entrée en fade dans le sens du régime
            entre_survente = self.rsi_prec >= SURVENTE and rsi < SURVENTE
            entre_surachat = self.rsi_prec <= SURACHAT and rsi > SURACHAT
            regime = self._regime()
            if entre_survente and regime > 0:
                viser(self, self.nq, CONTRATS)       # long : repli en tendance haussière
            elif entre_surachat and regime < 0:
                viser(self, self.nq, -CONTRATS)      # short : rebond en tendance baissière
        self.rsi_prec = rsi

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)
            if self.portfolio[self.nq].invested:
                self.prix_entree = float(event.fill_price)
            sens = "ACHAT " if event.fill_quantity > 0 else "VENTE "
            self.log(f"TRADE {self.nb_trades:>3} {sens}{event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"RSI={self.rsi.current.value:.1f} | qté={event.fill_quantity:+.0f} @ "
                     f"{event.fill_price} | frais={event.order_fee.value.amount:.2f} $")

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement_strat = equite / CAPITAL - 1
        self.log(f"--- BILAN RSI {PERIODE_RSI} ({SURVENTE}/{SURACHAT}) NQ signal 3 m, "
                 f"régime ≈ 2,5 h, long/short, cooldown {COOLDOWN_MIN}m ---")
        self.log(f"Trades exécutés : {self.nb_trades} | frais totaux : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement stratégie : {rendement_strat:+.4%}")
