"""Générateur de données synthétiques (aucune connexion IB).

Produit, pour chaque symbole, un flux de trades (prix en marche aléatoire, taille
et côté agresseur) et des snapshots de carnet animés, poussés dans les `FlowStore`.
Permet de voir footprint / heatmap / scatter / DOM bouger même marché fermé ou sans
abonnement L2 temps réel.

Piloté par un QTimer ; même interface de sortie (FlowStore) que le flux réel.
"""
from __future__ import annotations

import random
import time

from pyqtgraph.Qt import QtCore

import config

# Prix de référence et tick par instrument (approximatifs, 2026).
_BASE = {"ES": (5600.0, 0.25), "NQ": (20000.0, 0.25)}
_DEFAULT = (1000.0, 0.25)


class _SymState:
    def __init__(self, symbol: str) -> None:
        base, tick = _BASE.get(symbol, _DEFAULT)
        self.tick = tick
        self.mid = base
        self.drift = 0.0


class DemoFeed:
    """Alimente des FlowStore avec des ticks + carnet synthétiques."""

    def __init__(self, stores: dict, interval_ms: int = 80) -> None:
        self.stores = stores
        self.interval_ms = interval_ms
        self.state = {sym: _SymState(sym) for sym in stores}
        self.timer = QtCore.QTimer()
        self.timer.timeout.connect(self._step)

    def start(self) -> None:
        self._prefill(config.LIVE_SPAN_SECONDS)
        self.timer.start(self.interval_ms)

    def stop(self) -> None:
        self.timer.stop()

    # -- génération --------------------------------------------------------
    def _walk(self, st: _SymState) -> None:
        """Marche aléatoire du mid avec une légère persistance de tendance."""
        st.drift = st.drift * 0.95 + random.gauss(0.0, 0.4)
        st.mid += (st.drift + random.gauss(0.0, 1.0)) * st.tick
        st.mid = round(st.mid / st.tick) * st.tick

    def _book(self, st: _SymState):
        """Carnet de DOM_ROWS niveaux de chaque côté autour du mid."""
        half = st.tick
        best_bid = st.mid - half
        best_ask = st.mid + half
        bids, asks = [], []
        for i in range(config.DOM_ROWS):
            sz = max(1, int(random.gauss(120, 60) * (1.0 - i / (config.DOM_ROWS * 1.5))))
            bids.append((round(best_bid - i * st.tick, 4), sz))
            sz = max(1, int(random.gauss(120, 60) * (1.0 - i / (config.DOM_ROWS * 1.5))))
            asks.append((round(best_ask + i * st.tick, 4), sz))
        return bids, asks

    def _emit_trades(self, store, st: _SymState, ts_ms: int) -> None:
        """0..4 trades autour du mid, côté biaisé par la tendance courante."""
        for _ in range(random.randint(0, 4)):
            buy_bias = 0.5 + max(-0.35, min(0.35, st.drift * 0.15))
            side = "buy" if random.random() < buy_bias else "sell"
            off = random.randint(0, 2) * st.tick
            price = round(st.mid + (off if side == "buy" else -off), 4)
            size = max(1, int(abs(random.gauss(4, 6))))
            store.add_trade(price, size, side=side, ts_ms=ts_ms)

    def _step(self) -> None:
        ts_ms = int(time.time() * 1000)
        for sym, store in self.stores.items():
            st = self.state[sym]
            self._walk(st)
            self._emit_trades(store, st, ts_ms)
            bids, asks = self._book(st)
            store.add_book(bids, asks, ts_ms=ts_ms)

    def _prefill(self, seconds: float) -> None:
        """Backfill ~`seconds` d'historique pour que le 1er affichage soit déjà rempli."""
        now_ms = int(time.time() * 1000)
        step_ms = max(self.interval_ms, 1)
        n = int(seconds * 1000 / step_ms)
        for k in range(n, 0, -1):
            ts_ms = now_ms - k * step_ms
            for sym, store in self.stores.items():
                st = self.state[sym]
                self._walk(st)
                self._emit_trades(store, st, ts_ms)
                # snapshots espacés selon SNAPSHOT_MS (le throttle du store s'en charge)
                bids, asks = self._book(st)
                store.add_book(bids, asks, ts_ms=ts_ms)
