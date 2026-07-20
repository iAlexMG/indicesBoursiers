# Croisement de moyennes mobiles — SCALPING 1 m, multi-TF, long/short — porté sur NQ.
# PATRON de la refonte scalping (miroir de sma_croisement.py du frère crypto,
# re-calibrage anti-frais du 2026-07-17). Les leviers anti-churn sont IDENTIQUES :
#   - SIGNAL sur barres 15 m (le croisement se lit aux bornes de 15 min, ~15x moins
#     d'événements qu'en 1 m) ; exécution/stop restent en 1 m.
#   - RÉGIME de fond 15 m (SMA 48 ≈ 12 h) : on n'entre que dans le sens de la tendance.
#   - COOLDOWN de 60 min après chaque sortie (bride la fréquence de ré-entrée).
#   - STOP élargi à 1,5 % (les trades respirent -> moins de stops -> moins de churn).
#   - Sortie : croisement inverse OU stop. Long ET short.
# Ce qui change : l'instrument (nq_instrument : lecteur 1 m, frais fixes par contrat,
# multiplicateur 20, levier) et le sizing en CONTRATS entiers (±1 via viser).
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL

# ── Paramètres scalping (choisis A PRIORI — jamais après la courbe), = frère crypto
TF_SIGNAL = 15           # cadence du signal (minutes) : croisement lu sur barres 15 m
SMA_RAPIDE = 9           # SMA courte, barres 15 m (≈ 2,25 h)
SMA_LENTE = 21           # SMA longue, barres 15 m (≈ 5,25 h)
REGIME_N = 48            # SMA de régime sur barres 15 m (48 × 15 min ≈ 12 h de fond)
STOP_PCT = 0.015         # stop protecteur : 1,5 % contre la position
COOLDOWN_MIN = 60        # pas de nouvelle entrée dans les 60 min suivant une sortie
CONTRATS = 1             # granularité futures : 1 contrat NQ (long +1 / short -1)


class SmaCroisementNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # ── Indicateurs de SIGNAL sur barres 15 m (nourris à la main aux bornes de 15 min).
        self.sma_rapide = SimpleMovingAverage(SMA_RAPIDE)
        self.sma_lente = SimpleMovingAverage(SMA_LENTE)
        self.diff_prec = None
        # ── Filtre de RÉGIME 15 m : SMA de fond nourrie des mêmes closes 15 m.
        self.sma_regime = SimpleMovingAverage(REGIME_N)
        self.dernier_close_sig = None

        # ── Gestion de position (long/short) + stop + cooldown
        self.prix_entree = None    # prix du dernier fill d'entrée (pour le stop)
        self.temps_sortie = None   # horodatage de la dernière sortie (pour le cooldown)
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.premier_close = None
        self.dernier_close = None

    def _regime(self):
        """+1 régime haussier, -1 baissier, 0 pas encore prêt."""
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

        # 1) Stop protecteur — vérifié à CHAQUE barre 1 m (extrême intra-barre), prioritaire.
        pos = self.portfolio[self.nq]
        if pos.invested and self.prix_entree is not None:
            bas, haut = float(bar["low"]), float(bar["high"])
            if pos.is_long and bas <= self.prix_entree * (1 - STOP_PCT):
                self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t
            elif pos.is_short and haut >= self.prix_entree * (1 + STOP_PCT):
                self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t

        # 2) SIGNAL : uniquement aux bornes de 15 min (barres 15 m « manuelles », causales).
        if t.minute % TF_SIGNAL != 0:
            return
        self.dernier_close_sig = close
        self.sma_rapide.update(t, close)
        self.sma_lente.update(t, close)
        self.sma_regime.update(t, close)
        if not (self.sma_rapide.is_ready and self.sma_lente.is_ready):
            return

        diff = self.sma_rapide.current.value - self.sma_lente.current.value
        regime = self._regime()
        pos = self.portfolio[self.nq]            # relire (le stop a pu liquider)
        if self.diff_prec is not None:
            croise_haut = self.diff_prec <= 0 and diff > 0
            croise_bas = self.diff_prec >= 0 and diff < 0
            if croise_haut:
                if regime > 0 and not pos.is_long and self._cooldown_ok(t):
                    viser(self, self.nq, CONTRATS)       # long : croisement + régime haussier
                elif pos.is_short:
                    self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t
            elif croise_bas:
                if regime < 0 and not pos.is_short and self._cooldown_ok(t):
                    viser(self, self.nq, -CONTRATS)      # short : croisement + régime baissier
                elif pos.is_long:
                    self.liquidate(self.nq); self.prix_entree = None; self.temps_sortie = t
        self.diff_prec = diff

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)
            # mémorise le prix d'entrée quand on OUVRE/RENVERSE une position
            if self.portfolio[self.nq].invested:
                self.prix_entree = float(event.fill_price)
            sens = "ACHAT " if event.fill_quantity > 0 else "VENTE "
            self.log(f"TRADE {self.nb_trades:>3} {sens}{event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"qté={event.fill_quantity:+.0f} contrat(s) @ {event.fill_price} | "
                     f"frais={event.order_fee.value.amount:.2f} $")

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement_strat = equite / CAPITAL - 1
        self.log(f"--- BILAN Croisement SMA {SMA_RAPIDE}/{SMA_LENTE} NQ (signal 15 m, "
                 f"régime 12 h, long/short, cooldown {COOLDOWN_MIN}m) ---")
        self.log(f"Trades exécutés : {self.nb_trades} | frais totaux : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement stratégie : {rendement_strat:+.4%}")
