# Croisement SMA 50/200 — porté sur NQ (logique identique au frère, sizing en contrats).
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL

PERIODE_RAPIDE = 50
PERIODE_LENTE = 200
CONTRATS = 1


class SmaCroisementNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.sma_rapide = SimpleMovingAverage(PERIODE_RAPIDE)
        self.sma_lente = SimpleMovingAverage(PERIODE_LENTE)
        self.diff_prec = None
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

        self.sma_rapide.update(data[self.nq].end_time, close)
        self.sma_lente.update(data[self.nq].end_time, close)
        if not (self.sma_rapide.is_ready and self.sma_lente.is_ready):
            return

        diff = self.sma_rapide.current.value - self.sma_lente.current.value
        if self.diff_prec is not None:
            croise_haut = self.diff_prec <= 0 and diff > 0
            croise_bas = self.diff_prec >= 0 and diff < 0
            if croise_haut and not self.portfolio[self.nq].invested:
                self.market_order(self.nq, CONTRATS)
            elif croise_bas and self.portfolio[self.nq].invested:
                self.liquidate(self.nq)
        self.diff_prec = diff

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        self.log(f"--- BILAN SMA {PERIODE_RAPIDE}/{PERIODE_LENTE} NQ ---")
        self.log(f"Trades : {self.nb_trades} | frais : {self.frais_totaux:.2f} $")
        self.log(f"Equite finale : {equite:.2f} $ | rendement : {rendement:+.4%}")
