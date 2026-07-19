# Bollinger 20 / 2sigma (retour a la moyenne) — porte sur NQ.
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL

PERIODE_BB = 20
K_ECARTS = 2.0
CONTRATS = 1


class BollingerNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.bb = BollingerBands(PERIODE_BB, K_ECARTS, MovingAverageType.SIMPLE)
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

        self.bb.update(data[self.nq].end_time, close)
        if not self.bb.is_ready:
            return

        basse = float(self.bb.lower_band.current.value)
        mediane = float(self.bb.middle_band.current.value)
        investi = self.portfolio[self.nq].invested
        if not investi and close < basse:
            self.market_order(self.nq, CONTRATS)
        elif investi and close >= mediane:
            self.liquidate(self.nq)

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        self.log(f"--- BILAN Bollinger {PERIODE_BB}/{K_ECARTS}sigma NQ ---")
        self.log(f"Trades : {self.nb_trades} | frais : {self.frais_totaux:.2f} $")
        self.log(f"Equite finale : {equite:.2f} $ | rendement : {rendement:+.4%}")
