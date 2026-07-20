# Stratégie avancée (capstone) — SCALPING 1 m, multi-TF, long/short — portée sur NQ
# (miroir de strategie_avancee.py du frère crypto, re-calibrage du 2026-07-17).
# Réunir TOUT le cours : régime + momentum + sizing au risque + stop suiveur + take.
# Tout le SIGNAL passe sur barres 15 m (croisements + régime + ATR agrégé) ; seuls les
# déclenchements de stop/take sont lus en 1 m.
#   - RÉGIME de fond 15 m (SMA 48 ≈ 12 h) : on ne prend position que dans son sens.
#   - ENTRÉE : le MACD(12,26,9) croise DANS le sens du régime + RSI confirme (>50 long / <50 short).
#   - TAILLE : on risque 0,5 % du capital ; la distance au stop (3×ATR15m) fixe la quantité,
#     TRADUITE EN CONTRATS ENTIERS. ⚠️ Granularité NQ : ~500 $ de budget de risque face à
#     ~2-3 k$ de risque par contrat -> plancher à 1 contrat (risque réalisé > budget ;
#     MNQ (÷10) donnerait la granularité fine — instrument des ordres réels, Phase 5).
#     Garde-fou de MARGE : jamais plus que ce que le levier autorise (pendant du
#     garde-fou notionnel du frère).
#   - SORTIES : stop SUIVEUR 3×ATR15m, take 5×ATR15m (R:R ≈ 1,67), sortie si le régime casse.
#   - COOLDOWN 60 min. Long ET short.
from AlgorithmImports import *
from datetime import timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL, MULTIPLIER, LEVERAGE

TF_SIGNAL = 15            # cadence du signal (minutes) : tout le momentum sur barres 15 m
REGIME_N = 48             # SMA de régime de fond sur barres 15 m (≈ 12 h)
PERIODE_RSI = 9
SEUIL_RSI = 50
PERIODE_ATR = 14
STOP_MULT = 3.0           # stop suiveur : 3 × ATR15m de l'extrême close depuis l'entrée
TAKE_MULT = 5.0           # take-profit : entrée ± 5 × ATR15m (R:R ≈ 1,67)
RISQUE_PAR_TRADE = 0.005  # 0,5 % du capital risqué par position
COOLDOWN_MIN = 60         # pas de nouvelle entrée dans les 60 min après une sortie


class StrategieAvanceeNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # Indicateurs de SIGNAL sur barres 15 m (nourris à la main aux bornes de 15 min).
        self.regime = SimpleMovingAverage(REGIME_N)
        self.dernier_close_sig = None
        self.rsi = RelativeStrengthIndex(PERIODE_RSI, MovingAverageType.WILDERS)
        self.macd = MovingAverageConvergenceDivergence(12, 26, 9,
                                                       MovingAverageType.EXPONENTIAL)
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.o15 = self.h15 = self.l15 = None   # accumulateur OHLC de la fenêtre 15 min
        self.diff_prec = None

        # État de la position (None / réinitialisé quand on est à plat).
        self.entry_prix = None
        self.stop_prix = None
        self.take_prix = None
        self.plus_haut = None     # extrême close depuis l'entrée (long)
        self.plus_bas = None      # extrême close depuis l'entrée (short)
        self.temps_sortie = None
        self.raison = ""

        # Journal & mesure d'exposition.
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.sorties = {"STOP": 0, "TAKE": 0, "REGIME": 0}
        self.barres_total = 0
        self.barres_investi = 0
        self.premier_close = None
        self.dernier_close = None

    def _regime(self):
        if not self.regime.is_ready or self.dernier_close_sig is None:
            return 0
        return 1 if self.dernier_close_sig > self.regime.current.value else -1

    def _cooldown_ok(self, maintenant):
        return (self.temps_sortie is None
                or (maintenant - self.temps_sortie).total_seconds() >= COOLDOWN_MIN * 60)

    def _sortir(self, raison, t):
        self.raison = raison
        self.sorties[raison] += 1
        self.liquidate(self.nq)
        self.temps_sortie = t

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

        # Accumuler la barre 15 m.
        if self.o15 is None:
            self.o15, self.h15, self.l15 = float(bar["open"]), haut, bas
        else:
            self.h15 = max(self.h15, haut)
            self.l15 = min(self.l15, bas)

        pos = self.portfolio[self.nq]
        self.barres_total += 1
        if pos.invested:
            self.barres_investi += 1

        # 1) SORTIES stop/take vérifiées CHAQUE minute (extrême intra-barre) — risque prioritaire.
        if pos.invested and self.entry_prix is not None:
            if pos.is_long:
                if bas <= self.stop_prix:
                    self._sortir("STOP", t)
                elif haut >= self.take_prix:
                    self._sortir("TAKE", t)
            elif pos.is_short:
                if haut >= self.stop_prix:
                    self._sortir("STOP", t)
                elif bas <= self.take_prix:
                    self._sortir("TAKE", t)

        # 2) SIGNAL : tout sur barres 15 m (croisement, régime, ATR, ratchet, entrées).
        if t.minute % TF_SIGNAL != 0:
            return
        tb15 = TradeBar(t, self.nq, self.o15, self.h15, self.l15, close, 0.0,
                        timedelta(minutes=15))
        self.regime.update(t, close)
        self.rsi.update(t, close)
        self.macd.update(t, close)
        self.atr.update(tb15)
        self.dernier_close_sig = close
        self.o15 = self.h15 = self.l15 = None      # reset de l'accumulateur 15 m
        if not (self.regime.is_ready and self.rsi.is_ready
                and self.macd.is_ready and self.atr.is_ready):
            return

        atr = float(self.atr.current.value)
        regime = self._regime()
        diff = float(self.macd.current.value) - float(self.macd.signal.current.value)
        croise_haut = self.diff_prec is not None and self.diff_prec <= 0 and diff > 0
        croise_bas = self.diff_prec is not None and self.diff_prec >= 0 and diff < 0
        self.diff_prec = diff
        rsi = float(self.rsi.current.value)
        pos = self.portfolio[self.nq]              # relire (stop/take a pu liquider)

        if pos.invested:
            # Ratchet du stop suiveur + sortie si le régime casse.
            if pos.is_long:
                self.plus_haut = max(self.plus_haut, close)
                self.stop_prix = max(self.stop_prix, self.plus_haut - STOP_MULT * atr)
                if regime < 0:
                    self._sortir("REGIME", t)
            else:
                self.plus_bas = min(self.plus_bas, close)
                self.stop_prix = min(self.stop_prix, self.plus_bas + STOP_MULT * atr)
                if regime > 0:
                    self._sortir("REGIME", t)
            return

        # 3) ENTRÉE : régime 15 m + MACD croise dans son sens + RSI confirme + cooldown.
        distance_stop = STOP_MULT * atr                       # en points NQ
        if distance_stop <= 0 or not self._cooldown_ok(t):
            return
        equite = float(self.portfolio.total_portfolio_value)
        risque_par_contrat = distance_stop * MULTIPLIER       # en $
        contrats = int(round((equite * RISQUE_PAR_TRADE) / risque_par_contrat))
        # Garde-fou de marge (pendant du garde-fou notionnel du frère) + plancher 1 contrat.
        cap_marge = int(0.95 * equite * LEVERAGE / (close * MULTIPLIER))
        contrats = max(1, min(contrats, max(cap_marge, 1)))
        if regime > 0 and croise_haut and rsi > SEUIL_RSI:
            self.plus_haut = close
            self.stop_prix = close - distance_stop
            self.take_prix = close + TAKE_MULT * atr
            self.market_order(self.nq, contrats)
        elif regime < 0 and croise_bas and rsi < SEUIL_RSI:
            self.plus_bas = close
            self.stop_prix = close + distance_stop
            self.take_prix = close - TAKE_MULT * atr
            self.market_order(self.nq, -contrats)

    def on_order_event(self, event: OrderEvent):
        if event.status != OrderStatus.FILLED:
            return
        self.nb_trades += 1
        self.frais_totaux += float(event.order_fee.value.amount)
        pos = self.portfolio[self.nq]
        if pos.invested:
            self.entry_prix = float(event.fill_price)
            sens = "LONG " if event.fill_quantity > 0 else "SHORT"
            self.log(f"TRADE {self.nb_trades:>3} {sens} {event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"{event.fill_quantity:+.0f} contrat(s) @ {event.fill_price} | "
                     f"stop {self.stop_prix:,.0f} / take {self.take_prix:,.0f} | "
                     f"frais={event.order_fee.value.amount:.2f} $")
        else:
            sortie = float(event.fill_price)
            if self.entry_prix:
                etait_long = event.fill_quantity < 0   # on VEND pour clôturer un long
                gain = (sortie / self.entry_prix - 1) if etait_long else (self.entry_prix / sortie - 1)
            else:
                gain = 0.0
            self.log(f"TRADE {self.nb_trades:>3} SORTIE {event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"[{self.raison:<6}] @ {event.fill_price} | P&L={gain:+.2%} | "
                     f"frais={event.order_fee.value.amount:.2f} $")
            self.entry_prix = None
            self.stop_prix = None
            self.take_prix = None
            self.plus_haut = None
            self.plus_bas = None

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement_strat = equite / CAPITAL - 1
        exposition = self.barres_investi / self.barres_total if self.barres_total else 0.0
        self.log(f"--- BILAN STRATÉGIE AVANCÉE NQ (signal 15 m, régime 12 h, ATR15m, "
                 f"long/short, cooldown {COOLDOWN_MIN}m) ---")
        self.log(f"Trades : {self.nb_trades} | sorties : {self.sorties['STOP']} stop, "
                 f"{self.sorties['TAKE']} take, {self.sorties['REGIME']} régime | "
                 f"frais : {self.frais_totaux:.2f} $")
        self.log(f"Exposition : {exposition:.1%} des barres en position "
                 f"({self.barres_investi}/{self.barres_total})")
        self.log(f"Équité finale : {equite:.2f} $ | rendement stratégie : {rendement_strat:+.4%}")
