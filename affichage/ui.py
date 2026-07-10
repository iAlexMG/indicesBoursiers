"""Interface temps réel : onglets ES/NQ, chacun = graphe orderflow/footprint + DOM.

La fenêtre ne fait qu'orchestrer : un `FlowPanel` (flow_view.py) par symbole dans un
`QTabWidget`, rafraîchi périodiquement (seul l'onglet actif est redessiné -> perf).
Les données viennent des `FlowStore` (alimentés par market_data ou demo_feed).
"""
from pyqtgraph.Qt import QtCore, QtWidgets

import config
from flow_view import FlowPanel


class MainWindow(QtWidgets.QMainWindow):
    """Fenêtre principale : un onglet par symbole, rafraîchi par un QTimer."""

    def __init__(self, symbols, stores, ib=None, demo=None):
        super().__init__()
        self.setWindowTitle("IBKR — Orderflow / Footprint / DOM (ES & NQ)")
        self.resize(1400, 900)

        self.stores = stores
        self.ib = ib
        self.demo = demo

        self.tabs = QtWidgets.QTabWidget()
        self.panels = {}
        for symbol in symbols:
            panel = FlowPanel(symbol, stores[symbol])
            self.panels[symbol] = panel
            self.tabs.addTab(panel, symbol)
        self.setCentralWidget(self.tabs)

        self.timer = QtCore.QTimer()
        self.timer.timeout.connect(self.refresh)
        self.timer.start(config.UPDATE_INTERVAL_MS)

    def refresh(self):
        """Ne redessine que l'onglet actif (les autres se mettront à jour au switch)."""
        panel = self.tabs.currentWidget()
        if panel is not None:
            panel.refresh()

    def closeEvent(self, event):
        """Arrêt propre : stoppe le timer, le démo, déconnecte IB et arrête asyncio."""
        import asyncio

        self.timer.stop()
        if self.demo is not None:
            try:
                self.demo.stop()
            except Exception:  # noqa: BLE001
                pass
        if self.ib is not None:
            try:
                if self.ib.isConnected():
                    self.ib.disconnect()
            except Exception:  # noqa: BLE001
                pass
            try:
                asyncio.get_event_loop().stop()
            except Exception:  # noqa: BLE001
                pass
        event.accept()
