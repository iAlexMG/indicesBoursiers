# Buy & Hold NQ — 1er backtest NQ, sol de vérité (recalcul de l'équité à la main).
# Valide la chaîne : lecteur NQ + FeeModel/contrat + SymbolProperties (multiplicateur 20) + levier.
from AlgorithmImports import *
import os
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))  # importer nq_instrument (même dossier)
from nq_instrument import setup_nq, CAPITAL, MULTIPLIER

CONTRATS = 1   # futures : on tient 1 contrat entier (sizing fixe, cf. nq_instrument)


class BuyHoldNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.achete = False
        self.fill_prix = None
        self.frais_totaux = 0.0
        self.dernier_close = None

    def on_data(self, data: Slice):
        if self.nq not in data:
            return
        self.dernier_close = float(data[self.nq].value)
        if not self.achete:
            self.achete = True
            self.market_order(self.nq, CONTRATS)

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.fill_prix = float(event.fill_price)
            self.frais_totaux += float(event.order_fee.value.amount)
            self.log(f"FILL {event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"qté={event.fill_quantity} contrat(s) @ {event.fill_price} | "
                     f"frais={event.order_fee.value.amount:.2f} $")

    def on_end_of_algorithm(self):
        qte = float(self.portfolio[self.nq].quantity)
        cash = float(self.portfolio.cash)
        equite_lean = float(self.portfolio.total_portfolio_value)
        # Sol de vérité : équité = cash + qté × close × MULTIPLICATEUR (le *20 est le test clé).
        equite_main = cash + qte * self.dernier_close * MULTIPLIER
        pnl_points = (self.dernier_close - self.fill_prix) if self.fill_prix else 0.0
        self.log(f"Sol de vérité NQ : qté={qte} contrat(s) | fill={self.fill_prix} | "
                 f"dernier close={self.dernier_close} | P&L={pnl_points:+.2f} pts "
                 f"= {pnl_points * MULTIPLIER * qte:+.2f} $")
        self.log(f"Équité LEAN={equite_lean:.2f} vs main={equite_main:.2f} | "
                 f"écart={equite_lean - equite_main:+.6f} $ | frais={self.frais_totaux:.2f} $")
