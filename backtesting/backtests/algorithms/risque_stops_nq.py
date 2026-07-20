# Gestion du risque — SCALPING 1 m, multi-TF, long/short — porté sur NQ (miroir de
# risque_stops.py du frère crypto, re-calibrage du 2026-07-17 + correctif cadence 3 m).
# MÊME signal que rsi_retour_moyenne_nq.py, mais on SÉPARE la logique de signal (RSI)
# de la logique de risque : stops DIMENSIONNÉS À LA VOLATILITÉ (ATR).
# Leviers identiques au frère :
#   - SIGNAL RSI + ATR sur barres 3 m AGRÉGÉES (l'ATR a besoin de vraies barres OHLC :
#     open/high/low/close reconstruits sur la fenêtre de 3 min) ; stop/take en 1 m.
#   - stop = entrée ∓ ATR3m × 2 | take = entrée ± ATR3m × 3 (R:R ≈ 1,5).
#   - COOLDOWN 45 min après sortie.
# Sortie possible : STOP · TAKE · SIGNAL (RSI revenu à la moyenne). Long ET short.
# Ce qui change : l'instrument (nq_instrument) et le sizing en contrats entiers (±1).
from AlgorithmImports import *
from datetime import timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL

PERIODE_RSI = 9           # RSI court (sur barres 3 m ≈ 27 min)
SURVENTE = 30             # RSI < 30 = survente (desserré : 25/75 sous-tradait)
SURACHAT = 70             # RSI > 70 = surachat
MOYENNE = 50              # RSI revenu à ~50 = fin du retour à la moyenne (sortie SIGNAL)
TF_SIGNAL = 3             # cadence du signal (minutes) : RSI + ATR sur barres 3 m
REGIME_N = 50             # SMA de régime sur barres 3 m (≈ 2,5 h)
PERIODE_ATR = 14          # ATR (barres 3 m) pour dimensionner les stops à la volatilité
STOP_MULT = 2.0           # stop  = entrée ∓ ATR3m × 2,0
TAKE_MULT = 3.0           # take  = entrée ± ATR3m × 3,0  (R:R ≈ 1,5)
COOLDOWN_MIN = 45         # pas de nouvelle entrée dans les 45 min après une sortie
CONTRATS = 1              # granularité futures : 1 contrat NQ


class RisqueStopsNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # Le SIGNAL : RSI + filtre de régime, sur barres 3 m.
        self.rsi = RelativeStrengthIndex(PERIODE_RSI, MovingAverageType.WILDERS)
        self.rsi_prec = None
        self.sma_regime = SimpleMovingAverage(REGIME_N)
        self.dernier_close_sig = None
        # Le RISQUE : ATR sur barres 3 m agrégées + niveaux stop/take posés à l'entrée.
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.o3 = self.h3 = self.l3 = None    # accumulateur OHLC de la fenêtre 3 min
        self.prix_entree = None
        self.stop_prix = None
        self.take_prix = None
        self.temps_sortie = None
        self.raison = ""

        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.nb_stop = 0
        self.nb_take = 0
        self.nb_signal = 0
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

        # Accumuler la barre 3 m (open du 1er 1 m, high/low étendus).
        if self.o3 is None:
            self.o3, self.h3, self.l3 = float(bar["open"]), haut, bas
        else:
            self.h3 = max(self.h3, haut)
            self.l3 = min(self.l3, bas)

        # 1) EN POSITION : le RISQUE prioritaire (stop/take ATR, intra-barre, chaque minute).
        pos = self.portfolio[self.nq]
        if pos.invested and self.prix_entree is not None:
            if pos.is_long:
                if bas <= self.stop_prix:
                    self.raison = "STOP  "; self.nb_stop += 1
                    self.liquidate(self.nq); self.temps_sortie = t
                elif haut >= self.take_prix:
                    self.raison = "TAKE  "; self.nb_take += 1
                    self.liquidate(self.nq); self.temps_sortie = t
            elif pos.is_short:
                if haut >= self.stop_prix:
                    self.raison = "STOP  "; self.nb_stop += 1
                    self.liquidate(self.nq); self.temps_sortie = t
                elif bas <= self.take_prix:
                    self.raison = "TAKE  "; self.nb_take += 1
                    self.liquidate(self.nq); self.temps_sortie = t

        # 2) SIGNAL : RSI + ATR (barre 3 m) + régime, aux bornes de 3 min.
        if t.minute % TF_SIGNAL != 0:
            return
        tb3 = TradeBar(t, self.nq, self.o3, self.h3, self.l3, close, 0.0,
                       timedelta(minutes=TF_SIGNAL))
        self.atr.update(tb3)
        self.rsi.update(t, close)
        self.sma_regime.update(t, close)
        self.dernier_close_sig = close
        self.o3 = self.h3 = self.l3 = None     # reset de l'accumulateur 3 m
        if not (self.rsi.is_ready and self.atr.is_ready):
            return
        rsi = float(self.rsi.current.value)
        pos = self.portfolio[self.nq]           # relire (le risque a pu liquider)

        if pos.invested and self.prix_entree is not None:
            # sortie SIGNAL : RSI revenu à la moyenne
            if (pos.is_long and rsi >= MOYENNE) or (pos.is_short and rsi <= MOYENNE):
                self.raison = "SIGNAL"; self.nb_signal += 1
                self.liquidate(self.nq); self.temps_sortie = t
        elif self.rsi_prec is not None and self._cooldown_ok(t):
            entre_survente = self.rsi_prec >= SURVENTE and rsi < SURVENTE
            entre_surachat = self.rsi_prec <= SURACHAT and rsi > SURACHAT
            regime = self._regime()
            if entre_survente and regime > 0:
                viser(self, self.nq, CONTRATS)       # long : repli en tendance haussière
            elif entre_surachat and regime < 0:
                viser(self, self.nq, -CONTRATS)      # short : rebond en tendance baissière
        self.rsi_prec = rsi

    def on_order_event(self, event: OrderEvent):
        if event.status != OrderStatus.FILLED:
            return
        self.nb_trades += 1
        self.frais_totaux += float(event.order_fee.value.amount)
        pos = self.portfolio[self.nq]
        if pos.invested:
            # Entrée : on pose les niveaux stop/take à partir de l'ATR 3 m courant.
            e = float(event.fill_price)
            self.prix_entree = e
            atr = float(self.atr.current.value)
            if pos.is_long:
                self.stop_prix = e - STOP_MULT * atr
                self.take_prix = e + TAKE_MULT * atr
            else:
                self.stop_prix = e + STOP_MULT * atr
                self.take_prix = e - TAKE_MULT * atr
            sens = "ACHAT " if event.fill_quantity > 0 else "VENTE "
            self.log(f"TRADE {self.nb_trades:>3} {sens}{event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"RSI={self.rsi.current.value:.1f} ATR={atr:.1f} | @ {event.fill_price} | "
                     f"stop={self.stop_prix:.1f} take={self.take_prix:.1f} | "
                     f"frais={event.order_fee.value.amount:.2f} $")
        else:
            sortie = float(event.fill_price)
            if self.prix_entree:
                etait_long = event.fill_quantity < 0    # on VEND pour clôturer un long
                gain = (sortie / self.prix_entree - 1) if etait_long else (self.prix_entree / sortie - 1)
            else:
                gain = 0.0
            self.log(f"TRADE {self.nb_trades:>3} SORTIE {event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"[{self.raison}] @ {event.fill_price} | P&L={gain:+.2%} | "
                     f"frais={event.order_fee.value.amount:.2f} $")
            self.prix_entree = self.stop_prix = self.take_prix = None

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement_strat = equite / CAPITAL - 1
        self.log(f"--- BILAN RSI {PERIODE_RSI} + RISQUE ATR{PERIODE_ATR} NQ (3 m, "
                 f"stop ×{STOP_MULT} / take ×{TAKE_MULT}), régime ≈ 2,5 h, long/short, "
                 f"cooldown {COOLDOWN_MIN}m ---")
        self.log(f"Trades : {self.nb_trades} | sorties : {self.nb_stop} stop, "
                 f"{self.nb_take} take, {self.nb_signal} signal | frais : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement stratégie : {rendement_strat:+.4%}")
