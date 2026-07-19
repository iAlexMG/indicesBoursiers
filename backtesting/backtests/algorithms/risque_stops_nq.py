# RSI 14 + stop/take (8%/10%) — porte sur NQ.
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL

PERIODE_RSI = 14
SURVENTE = 30
SURACHAT = 70
STOP = 0.08
TAKE = 0.10
CONTRATS = 1


class RisqueStopsNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.rsi = RelativeStrengthIndex(PERIODE_RSI, MovingAverageType.WILDERS)
        self.rsi_prec = None
        self.entry_prix = None
        self.raison = ""
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.nb_stop = self.nb_take = self.nb_signal = 0
        self.premier_close = None
        self.dernier_close = None

    def on_data(self, data: Slice):
        if self.nq not in data:
            return
        close = float(data[self.nq].value)
        if self.premier_close is None:
            self.premier_close = close
        self.dernier_close = close

        self.rsi.update(data[self.nq].end_time, close)
        if not self.rsi.is_ready:
            return
        rsi = float(self.rsi.current.value)

        investi = self.portfolio[self.nq].invested
        if investi and self.entry_prix is not None:
            if close <= self.entry_prix * (1 - STOP):
                self.raison = "STOP"; self.nb_stop += 1
                self.liquidate(self.nq)
            elif close >= self.entry_prix * (1 + TAKE):
                self.raison = "TAKE"; self.nb_take += 1
                self.liquidate(self.nq)

        investi = self.portfolio[self.nq].invested
        if self.rsi_prec is not None:
            entre_survente = self.rsi_prec >= SURVENTE and rsi < SURVENTE
            entre_surachat = self.rsi_prec <= SURACHAT and rsi > SURACHAT
            if entre_survente and not investi:
                self.market_order(self.nq, CONTRATS)
            elif entre_surachat and investi:
                self.raison = "SIGNAL"; self.nb_signal += 1
                self.liquidate(self.nq)
        self.rsi_prec = rsi

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)
            if event.fill_quantity > 0:
                self.entry_prix = float(event.fill_price)
            else:
                self.entry_prix = None

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        self.log(f"--- BILAN RSI+RISQUE (stop {STOP:.0%}/take {TAKE:.0%}) NQ ---")
        self.log(f"Trades : {self.nb_trades} | sorties: {self.nb_stop} stop, "
                 f"{self.nb_take} take, {self.nb_signal} signal | frais : {self.frais_totaux:.2f} $")
        self.log(f"Equite finale : {equite:.2f} $ | rendement : {rendement:+.4%}")
