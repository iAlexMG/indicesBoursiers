"""Point d'entrée : lance l'UI orderflow/footprint/DOM (ES & NQ).

Deux modes (config.DEMO_MODE) :
  - DEMO  : générateur de données synthétiques, aucune connexion IB.
  - RÉEL  : connexion TWS/IB Gateway, flux temps réel ES/NQ.

Lancement :  python main.py
"""

import sys

from pyqtgraph.Qt import QtWidgets

import config
from orderflow_data import FlowStore
from ui import MainWindow


def run_demo(app):
    """Mode démo : FlowStore alimentés par un générateur synthétique (QTimer)."""
    from demo_feed import DemoFeed

    stores = {sym: FlowStore(sym) for sym in config.SYMBOLS}
    demo = DemoFeed(stores)
    demo.start()

    window = MainWindow(config.SYMBOLS, stores, demo=demo)
    window.show()
    app.exec_()


def run_live(app):
    """Mode réel : connexion IB, souscriptions, FlowStore alimentés en continu."""
    from ib_insync import util

    from ib_connection import connect, resolve_front_month
    from market_data import MarketDataManager

    ib = connect()

    contracts = {}
    stores = {}
    for symbol in config.SYMBOLS:
        contracts[symbol] = resolve_front_month(ib, symbol)
        stores[symbol] = FlowStore(symbol)

    market_data = MarketDataManager(ib, contracts, stores)
    market_data.subscribe()

    window = MainWindow(config.SYMBOLS, stores, ib=ib)
    window.show()

    # Intègre la boucle Qt dans la boucle asyncio d'ib_insync, puis fait tourner
    # cette boucle (gère à la fois IB et l'UI).
    util.useQt()
    try:
        ib.run()
    except KeyboardInterrupt:
        pass
    finally:
        if ib.isConnected():
            ib.disconnect()


def main():
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    if config.DEMO_MODE:
        run_demo(app)
    else:
        run_live(app)


if __name__ == "__main__":
    main()
