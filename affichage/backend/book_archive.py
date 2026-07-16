"""Archive des snapshots de carnet sur disque (SQLite) pour la heatmap historique.

Porté de `crypto/affichage/backend/book_archive.py`. La contrainte qui l'a fait naître est
IDENTIQUE des deux côtés, pour des raisons différentes : le carnet n'a aucun historique à
télécharger. Chez crypto, aucun exchange ne sert d'historique REST de carnet ; ici,
`HistoryType` de Quantower ne connaît que `Bid|Ask|Midpoint|Last|BidAsk|Mark` — **aucun L2**
(mesuré par réflexion sur BusinessLayer v1.146.14). La seule façon de constituer un historique
de liquidité est donc de PERSISTER les snapshots live au fil de l'eau : l'historique du carnet
ne grandit que VERS L'AVANT, pendant les sessions où l'app tourne.

Base SÉPARÉE de `trades.db` (`books.db`) : volume et schéma très différents, cycles de vie
indépendants (purgeable sans toucher aux trades). WAL + busy_timeout car on écrit pendant que
le rendu de la heatmap lit.

Différence avec le frère : la 1re colonne est la **source** (`quantower`/`ibkr`/`demo`) là où
crypto porte le `market` (l'exchange × marché). Même rôle — la clé du flux — mais ici les
sources ne sont pas des marchés distincts : ce sont deux portes vers le MÊME carnet du CME.
Les garder séparées permet justement de les comparer.
"""
from __future__ import annotations

import sqlite3
import threading

import numpy as np

# dtypes des niveaux sérialisés (prix f64, tailles f32) — mêmes que le frère.
_PRICE_DT = np.float64
_SIZE_DT = np.float32


class BookSnapshot:
    __slots__ = ("ts", "bid", "ask", "prices", "sizes")

    def __init__(self, ts: int, bid: float, ask: float,
                 prices: np.ndarray, sizes: np.ndarray) -> None:
        self.ts = ts            # ms UTC
        self.bid = bid          # best bid au moment du snapshot
        self.ask = ask          # best ask
        self.prices = prices    # niveaux : prix (bids + asks)
        self.sizes = sizes      # niveaux : tailles


class BookArchive:
    def __init__(self, path: str = "books.db") -> None:
        self._path = path
        self._conn = sqlite3.connect(path, check_same_thread=False)
        self._lock = threading.Lock()      # protège la connexion d'ÉCRITURE
        self._closed = False
        # Lectures = connexion read-only PAR THREAD, hors du lock d'écriture : le
        # recorder écrit pendant que le lecteur de heatmap lit, sans se bloquer.
        self._readers = threading.local()
        self._read_conns: list[sqlite3.Connection] = []
        self._read_conns_lock = threading.Lock()
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA busy_timeout=5000")
        self._conn.execute("PRAGMA synchronous=NORMAL")
        self._conn.execute(
            "CREATE TABLE IF NOT EXISTS snapshots("
            "source TEXT, symbol TEXT, ts INTEGER, bid REAL, ask REAL, "
            "prices BLOB, sizes BLOB, "
            "PRIMARY KEY(source, symbol, ts))"
        )
        self._conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_snap_ts ON snapshots(source, symbol, ts)"
        )
        self._conn.commit()

    def _reader(self) -> sqlite3.Connection:
        """Connexion read-only propre au thread appelant (créée à la demande)."""
        conn = getattr(self._readers, "conn", None)
        if conn is None:
            conn = sqlite3.connect(self._path, check_same_thread=False)
            conn.execute("PRAGMA query_only=1")
            conn.execute("PRAGMA busy_timeout=5000")
            self._readers.conn = conn
            with self._read_conns_lock:
                self._read_conns.append(conn)
        return conn

    def insert_snapshot(self, source: str, symbol: str, ts: int,
                        bid: float, ask: float,
                        prices: np.ndarray, sizes: np.ndarray) -> None:
        """Persiste une colonne de carnet. INSERT OR IGNORE : un même (source, symbol, ts)
        n'est écrit qu'une fois. Dédupliquer est SANS RISQUE ici, contrairement aux trades :
        deux snapshots du même carnet à la même milliseconde sont forcément le même état."""
        blob_p = np.ascontiguousarray(prices, dtype=_PRICE_DT).tobytes()
        blob_s = np.ascontiguousarray(sizes, dtype=_SIZE_DT).tobytes()
        with self._lock:
            if self._closed:
                return
            self._conn.execute(
                "INSERT OR IGNORE INTO snapshots VALUES (?,?,?,?,?,?,?)",
                (source, symbol, int(ts), float(bid), float(ask), blob_p, blob_s))
            self._conn.commit()

    def query_sampled(self, source: str, symbol: str,
                      t0_ms: int, t1_ms: int, max_cols: int) -> list[BookSnapshot]:
        """Lecture BORNÉE : au plus ~max_cols snapshots RÉPARTIS sur [t0, t1], quel que soit
        le nombre réel de lignes dans la plage. On découpe la fenêtre en max_cols tranches
        égales et on prend UN snapshot par tranche (le plus ancien). Le coût dépend de la
        PLAGE demandée, pas de la taille de la base → pas de gel même sur une grosse base.

        (MIN(ts) + colonnes nues : SQLite renvoie les valeurs de la LIGNE qui porte ce MIN(ts)
        dans chaque groupe → un vrai snapshot, pas un mélange de plusieurs.)
        """
        span = max(t1_ms - t0_ms, 1)
        bucket = max(1, span // max(1, max_cols))
        if self._closed:
            return []
        cur = self._reader().execute(
            "SELECT MIN(ts) AS ts, bid, ask, prices, sizes FROM snapshots "
            "WHERE source=? AND symbol=? AND ts>=? AND ts<=? "
            "GROUP BY (ts - ?) / ? ORDER BY ts",
            (source, symbol, t0_ms, t1_ms, t0_ms, bucket))
        return [BookSnapshot(ts, bid, ask,
                             np.frombuffer(bp, dtype=_PRICE_DT),
                             np.frombuffer(bs, dtype=_SIZE_DT))
                for (ts, bid, ask, bp, bs) in cur.fetchall()]

    def earliest(self, source: str, symbol: str) -> int | None:
        if self._closed:
            return None
        cur = self._reader().execute(
            "SELECT MIN(ts) FROM snapshots WHERE source=? AND symbol=?", (source, symbol))
        row = cur.fetchone()
        return row[0] if row and row[0] is not None else None

    def count(self, source: str, symbol: str) -> int:
        if self._closed:
            return 0
        cur = self._reader().execute(
            "SELECT COUNT(*) FROM snapshots WHERE source=? AND symbol=?", (source, symbol))
        return int(cur.fetchone()[0])

    def purge(self, cutoff_ms: int) -> int:
        """Rétention : supprime les snapshots plus vieux que cutoff_ms."""
        with self._lock:
            if self._closed:
                return 0
            n = self._conn.execute(
                "DELETE FROM snapshots WHERE ts < ?", (cutoff_ms,)).rowcount
            self._conn.commit()
        return n

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            self._conn.close()
        with self._read_conns_lock:
            for c in self._read_conns:
                try:
                    c.close()
                except sqlite3.Error:
                    pass
            self._read_conns.clear()
