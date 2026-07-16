"""Enregistreur disque : écriture DIFFÉRÉE des trades et des snapshots de carnet.

Un seul thread écrit, alimenté par une file. Les sources (pont Quantower, IBKR, démo) ne font
qu'enfiler — elles ne touchent jamais SQLite. C'est la même règle que côté C# dans le pont
(`NqFeedStrategy`) : **le thread qui reçoit le marché ne doit jamais bloquer sur une I/O**.
Ici le risque est réel et pas théorique : un `commit` SQLite fait un fsync, qui peut coûter des
dizaines de millisecondes si le disque est occupé — assez pour prendre du retard sur le flux.

La file est BORNÉE. Si elle déborde, on JETTE et on compte (`dropped`) plutôt que de faire
remonter la pression jusqu'au flux de marché : perdre une ligne d'archive est regrettable,
retarder l'affichage temps réel ne l'est pas.

Rétention : purge périodique au-delà de `retention_days`, dans ce même thread — les bases ne
grossissent pas sans fin. Chez le frère c'est `rollup_maintainer.py` qui s'en charge ; ici il
n'y a pas de rollup à drainer, donc la purge vit avec l'écrivain.
"""
from __future__ import annotations

import logging
import queue
import threading
import time

import numpy as np

log = logging.getLogger("recorder")

_BATCH_MAX = 500          # trades écrits par commit (groupage = 1 seul fsync)
_DRAIN_MS = 250           # latence max avant d'écrire un lot partiel
_PURGE_EVERY_S = 3600.0   # une passe de rétention par heure


class Recorder:
    def __init__(self, trade_archive, book_archive, retention_days: float = 7.0,
                 maxsize: int = 50_000) -> None:
        self.trades = trade_archive
        self.books = book_archive
        self.retention_days = retention_days
        self._q: queue.Queue = queue.Queue(maxsize=maxsize)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.n_trades = 0
        self.n_books = 0
        self.dropped = 0

    # -- API des sources (n'importe quel thread) --------------------------
    def trade(self, source: str, symbol: str, ts: int, price: float,
              size: float, side: str) -> None:
        self._put(("t", (source, symbol, int(ts), float(price), float(size), side)))

    def book(self, source: str, symbol: str, ts: int, bids, asks) -> None:
        """`bids`/`asks` : listes de (prix, taille), meilleur niveau en premier."""
        if not bids and not asks:
            return
        best_bid = bids[0][0] if bids else 0.0
        best_ask = asks[0][0] if asks else 0.0
        levels = list(bids) + list(asks)
        prices = np.array([p for p, _ in levels], dtype=np.float64)
        sizes = np.array([s for _, s in levels], dtype=np.float32)
        self._put(("b", (source, symbol, int(ts), best_bid, best_ask, prices, sizes)))

    def _put(self, item) -> None:
        try:
            self._q.put_nowait(item)
        except queue.Full:
            self.dropped += 1

    # -- cycle de vie ------------------------------------------------------
    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="recorder", daemon=True)
        self._thread.start()

    def stop(self, timeout: float = 5.0) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=timeout)
            self._thread = None

    # -- thread d'écriture -------------------------------------------------
    def _run(self) -> None:
        next_purge = time.time() + _PURGE_EVERY_S
        while not self._stop.is_set():
            self._drain_once()
            if time.time() >= next_purge:
                next_purge = time.time() + _PURGE_EVERY_S
                self._purge()
        self._drain_once(final=True)   # ne pas perdre ce qui reste en file à l'arrêt

    def _drain_once(self, final: bool = False) -> None:
        """Vide la file en groupant les trades : un commit par lot, pas par trade."""
        pending_trades: list[tuple] = []
        deadline = time.time() + _DRAIN_MS / 1000.0
        while True:
            timeout = 0.0 if final else max(0.0, deadline - time.time())
            try:
                kind, payload = self._q.get(timeout=timeout or 0.05)
            except queue.Empty:
                break
            if kind == "t":
                pending_trades.append(payload)
                if len(pending_trades) >= _BATCH_MAX:
                    break
            else:
                # Les snapshots arrivent ~4/s : pas de groupage nécessaire, et les écrire
                # tout de suite évite de gonfler la file avec de gros tableaux numpy.
                try:
                    self.books.insert_snapshot(*payload)
                    self.n_books += 1
                except Exception:
                    log.exception("écriture d'un snapshot de carnet")
            if final and self._q.empty():
                break
        if pending_trades:
            try:
                self.n_trades += self.trades.insert_many(pending_trades)
            except Exception:
                log.exception("écriture d'un lot de trades")

    def _purge(self) -> None:
        cutoff = int((time.time() - self.retention_days * 86400.0) * 1000)
        try:
            nt = self.trades.purge(cutoff)
            nb = self.books.purge(cutoff)
            if nt or nb:
                log.info("rétention : %d trades et %d snapshots purgés (> %.0f j)",
                         nt, nb, self.retention_days)
        except Exception:
            log.exception("purge de rétention")
