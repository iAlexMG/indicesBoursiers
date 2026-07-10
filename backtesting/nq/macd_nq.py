# MACD 12/26/9 (croisement) — porté sur NQ.
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL

RAPIDE, LENTE, SIGNAL = 12, 26, 9
CONTRATS = 1


class MacdNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.macd = MovingAverageConvergenceDivergence(RAPIDE, LENTE, SIGNAL,
                                                        MovingAverageType.EXPONENTIAL)
        self.hist_prec = None
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.premier_close = None
        self.dernier_close = None

    def on_data(self, data: Slice):
        if self.nq not in data:
            return
        close = float(data[self.nq].value)
        if self.premier_close is None:
            self.premier_close = close
        self.dernier_close = close

        self.macd.update(data[self.nq].end_time, close)
        if not self.macd.is_ready:
            return

        hist = float(self.macd.current.value) - float(self.macd.signal.current.value)
        if self.hist_prec is not None:
            croise_haut = self.hist_prec <= 0 and hist > 0
            croise_bas = self.hist_prec >= 0 and hist < 0
            if croise_haut and not self.portfolio[self.nq].invested:
                self.market_order(self.nq, CONTRATS)
            elif croise_bas and self.portfolio[self.nq].invested:
                self.liquidate(self.nq)
        self.hist_prec = hist

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        self.log(f"--- BILAN MACD ({RAPIDE},{LENTE},{SIGNAL}) NQ ---")
        self.log(f"Trades : {self.nb_trades} | frais : {self.frais_totaux:.2f} $")
        self.log(f"Equite finale : {equite:.2f} $ | rendement : {rendement:+.4%}")
