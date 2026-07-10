"""Souscription aux données temps réel : ticks (trades) et carnet d'ordres (DOM).

Route le flux IB vers les `FlowStore` (orderflow_data) consommés par la vue :
- chaque nouveau `last` -> un trade (côté inféré via bid/ask),
- le carnet (`domBids`/`domAsks`) -> snapshots pour la heatmap et l'échelle DOM.
"""
import math

import config


def _valid(x):
    return x is not None and not (isinstance(x, float) and math.isnan(x))


class MarketDataManager:
    """Gère les souscriptions market data + DOM et alimente les FlowStore."""

    def __init__(self, ib, contracts, stores):
        self.ib = ib
        self.contracts = contracts          # {symbol: Contract}
        self.stores = stores                # {symbol: FlowStore}
        self.tickers = {}                   # {symbol: Ticker} (prix)
        self.dom_tickers = {}               # {symbol: Ticker} (carnet d'ordres)
        self._last_seen = {}                # {symbol: (price, time)} -> dédup des trades

    def subscribe(self):
        """Lance les souscriptions et branche le handler de mise à jour des ticks."""
        for symbol, contract in self.contracts.items():
            self.tickers[symbol] = self.ib.reqMktData(contract, "", False, False)
            self.dom_tickers[symbol] = self.ib.reqMktDepth(contract, numRows=config.DOM_ROWS)
        self.ib.pendingTickersEvent += self._on_pending_tickers

    def _on_pending_tickers(self, tickers):
        """À chaque mise à jour : enregistre le trade (si nouveau) + un snapshot carnet."""
        updated = set(tickers)
        for symbol, ticker in self.tickers.items():
            store = self.stores.get(symbol)
            if store is None:
                continue
            dom = self.dom_tickers.get(symbol)
            if dom is not None and dom in updated:
                store.add_book(list(dom.domBids), list(dom.domAsks))
            if ticker not in updated:
                continue
            last = ticker.last if _valid(ticker.last) else ticker.marketPrice()
            if not _valid(last):
                continue
            stamp = (last, getattr(ticker, "time", None))
            if self._last_seen.get(symbol) == stamp:
                continue                    # même trade -> on n'ajoute pas de doublon
            self._last_seen[symbol] = stamp
            size = ticker.lastSize if _valid(ticker.lastSize) and ticker.lastSize > 0 else 1
            store.add_trade(last, size,
                            bid=ticker.bid if _valid(ticker.bid) else None,
                            ask=ticker.ask if _valid(ticker.ask) else None)

    def dom_for(self, symbol):
        """Renvoie (bids, asks) du carnet d'ordres courant (listes de DOMLevel)."""
        ticker = self.dom_tickers.get(symbol)
        if ticker is None:
            return [], []
        return list(ticker.domBids), list(ticker.domAsks)
