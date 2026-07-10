# Strategie avancee (regime SMA200 + MACD confirme RSI>50 + sizing au risque + stop
# suiveur 2xATR / take 4xATR) — portee sur NQ. Sizing traduit en CONTRATS entiers :
#   contrats = (equite * 1%) / (distance_stop_points * multiplicateur), arrondi, min 1.
# NB : sur 100 k$, le 1 % de risque NQ arrondit souvent a 0-1 contrat -> plancher a 1
# (risque realise parfois > 1 %). MNQ (÷10) donnerait une granularite fine -> instrument
# des ordres reels (Phase 5). Logique de signal identique au frere.
from AlgorithmImports import *
from datetime import timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL, MULTIPLIER

PERIODE_TENDANCE = 200
PERIODE_RSI = 14
SEUIL_RSI = 50
PERIODE_ATR = 14
STOP_MULT = 2.0
TAKE_MULT = 4.0
RISQUE_PAR_TRADE = 0.01


class StrategieAvanceeNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.tendance = SimpleMovingAverage(PERIODE_TENDANCE)
        self.rsi = RelativeStrengthIndex(PERIODE_RSI, MovingAverageType.WILDERS)
        self.macd = MovingAverageConvergenceDivergence(12, 26, 9, MovingAverageType.EXPONENTIAL)
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.diff_prec = None
        self.entry_prix = self.stop_prix = self.take_prix = self.plus_haut = None
        self.raison = ""
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.sorties = {"STOP": 0, "TAKE": 0, "REGIME": 0}
        self.barres_total = 0
        self.barres_investi = 0
        self.premier_close = None
        self.dernier_close = None

    def on_data(self, data: Slice):
        if self.nq not in data:
            return
        bar = data[self.nq]
        close = float(bar.value)
        if self.premier_close is None:
            self.premier_close = close
        self.dernier_close = close

        t = bar.end_time
        self.tendance.update(t, close)
        self.rsi.update(t, close)
        self.macd.update(t, close)
        tb = TradeBar(bar.time, self.nq, float(bar["open"]), float(bar["high"]),
                      float(bar["low"]), close, float(bar["volume"]), timedelta(hours=1))
        self.atr.update(tb)
        if not (self.tendance.is_ready and self.rsi.is_ready
                and self.macd.is_ready and self.atr.is_ready):
            return

        atr = float(self.atr.current.value)
        regime_haussier = close > float(self.tendance.current.value)
        diff = float(self.macd.current.value) - float(self.macd.signal.current.value)
        croise_haut = self.diff_prec is not None and self.diff_prec <= 0 and diff > 0
        self.diff_prec = diff
        investi = self.portfolio[self.nq].invested
        self.barres_total += 1
        if investi:
            self.barres_investi += 1

        # Sorties d'abord (le risque prime).
        if investi:
            self.plus_haut = max(self.plus_haut, close)
            nouveau_stop = self.plus_haut - STOP_MULT * atr
            if nouveau_stop > self.stop_prix:
                self.stop_prix = nouveau_stop
            if close <= self.stop_prix:
                self.raison = "STOP"
            elif close >= self.take_prix:
                self.raison = "TAKE"
            elif not regime_haussier:
                self.raison = "REGIME"
            else:
                return
            self.sorties[self.raison] += 1
            self.liquidate(self.nq)
            return

        # Entree : regime haussier + croisement MACD + RSI > 50.
        if not (regime_haussier and croise_haut and float(self.rsi.current.value) > SEUIL_RSI):
            return

        # Taille au risque, traduite en contrats entiers.
        equite = float(self.portfolio.total_portfolio_value)
        distance_stop = STOP_MULT * atr                       # en points
        risque_par_contrat = distance_stop * MULTIPLIER       # en $
        if risque_par_contrat <= 0:
            return
        contrats = max(1, int(round((equite * RISQUE_PAR_TRADE) / risque_par_contrat)))
        self.plus_haut = close
        self.stop_prix = close - distance_stop
        self.take_prix = close + TAKE_MULT * atr
        self.market_order(self.nq, contrats)

    def on_order_event(self, event: OrderEvent):
        if event.status != OrderStatus.FILLED:
            return
        self.nb_trades += 1
        self.frais_totaux += float(event.order_fee.value.amount)
        if event.fill_quantity > 0:
            self.entry_prix = float(event.fill_price)
        else:
            self.entry_prix = self.stop_prix = self.take_prix = self.plus_haut = None

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        expo = self.barres_investi / self.barres_total if self.barres_total else 0.0
        self.log(f"--- BILAN STRATEGIE AVANCEE NQ ---")
        self.log(f"Trades : {self.nb_trades} | sorties : {self.sorties['STOP']} stop, "
                 f"{self.sorties['TAKE']} take, {self.sorties['REGIME']} regime | "
                 f"frais : {self.frais_totaux:.2f} $")
        self.log(f"Exposition : {expo:.1%} ({self.barres_investi}/{self.barres_total})")
        self.log(f"Equite finale : {equite:.2f} $ | rendement : {rendement:+.4%}")
