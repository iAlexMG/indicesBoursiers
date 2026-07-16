"""Archive des trades sur disque (SQLite) — orderflow historique du footprint.

Inspiré de `crypto/affichage/backend/archive.py`, mais **délibérément plus simple**, et sur un
point la divergence est structurelle, pas cosmétique :

⚠️ **AUCUNE DÉDUPLICATION.** Crypto a une `PRIMARY KEY(market, symbol, trade_id)` parce que son
collecteur REST recomble des trous et recouvre le live : sans clé, un même trade entrerait deux
fois. Ici, rien de tel :
  - **Rithmic ne fournit pas de `TradeId`** (mesuré par la sonde du 2026-07-15, et déjà
    documenté par `historique/NqExtractor` : « pas de TradeId → rowid auto ») ;
  - il n'existe **aucun backfill** possible du live (pas d'historique L2, et les ticks passés
    sont le métier du pilier Historique, pas de celui-ci) — chaque trade n'arrive qu'une fois.
Et surtout : **deux trades de 1 lot au même prix, au même côté, à la même milliseconde sont
RÉELS et DISTINCTS**. Une clé primaire sur `(ts, price, size, side)` les fusionnerait et
sous-estimerait le volume — exactement ce que le footprint est censé mesurer. La table est donc
en AJOUT PUR, `rowid` implicite.

Pas de rollup pré-agrégé non plus, pour l'instant : crypto en a besoin pour dézoomer sur 90
jours de 7 exchanges. Ici on garde quelques jours d'un seul instrument. À MESURER avant de
construire : si le zoom arrière gèle, le rollup du frère est là pour être porté.
"""
from __future__ import annotations

import sqlite3
import threading


class TradeArchive:
    def __init__(self, path: str = "trades.db") -> None:
        self._path = path
        self._conn = sqlite3.connect(path, check_same_thread=False)
        self._lock = threading.Lock()
        self._closed = False
        self._readers = threading.local()
        self._read_conns: list[sqlite3.Connection] = []
        self._read_conns_lock = threading.Lock()
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA busy_timeout=5000")
        self._conn.execute("PRAGMA synchronous=NORMAL")
        # Pas de PRIMARY KEY : voir l'en-tête du module. `rowid` implicite, ordre d'insertion
        # = ordre d'arrivée. L'index sur (source, symbol, ts) porte toutes les lectures.
        self._conn.execute(
            "CREATE TABLE IF NOT EXISTS trades("
            "source TEXT, symbol TEXT, ts INTEGER, price REAL, size REAL, side TEXT)"
        )
        self._conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_trades_ts ON trades(source, symbol, ts)"
        )
        self._conn.commit()

    def _reader(self) -> sqlite3.Connection:
        conn = getattr(self._readers, "conn", None)
        if conn is None:
            conn = sqlite3.connect(self._path, check_same_thread=False)
            conn.execute("PRAGMA query_only=1")
            conn.execute("PRAGMA busy_timeout=5000")
            self._readers.conn = conn
            with self._read_conns_lock:
                self._read_conns.append(conn)
        return conn

    def insert_many(self, rows) -> int:
        """Ajoute des trades. `rows` = itérable de (source, symbol, ts, price, size, side).
        Écriture GROUPÉE : un seul commit par lot, sinon SQLite fsync à chaque trade et le
        thread d'écriture ne tient pas les ~9 trades/s (mesurés) une fois la base grosse."""
        rows = list(rows)
        if not rows:
            return 0
        with self._lock:
            if self._closed:
                return 0
            self._conn.executemany("INSERT INTO trades VALUES (?,?,?,?,?,?)", rows)
            self._conn.commit()
        return len(rows)

    def query(self, source: str, symbol: str, t0_ms: int, t1_ms: int) -> list[tuple]:
        """Trades de la fenêtre, par ts croissant : (ts, price, size, side)."""
        if self._closed:
            return []
        cur = self._reader().execute(
            "SELECT ts, price, size, side FROM trades "
            "WHERE source=? AND symbol=? AND ts>=? AND ts<=? ORDER BY ts",
            (source, symbol, t0_ms, t1_ms))
        return cur.fetchall()

    def earliest(self, source: str, symbol: str) -> int | None:
        if self._closed:
            return None
        cur = self._reader().execute(
            "SELECT MIN(ts) FROM trades WHERE source=? AND symbol=?", (source, symbol))
        row = cur.fetchone()
        return row[0] if row and row[0] is not None else None

    def count(self, source: str, symbol: str) -> int:
        if self._closed:
            return 0
        cur = self._reader().execute(
            "SELECT COUNT(*) FROM trades WHERE source=? AND symbol=?", (source, symbol))
        return int(cur.fetchone()[0])

    def purge(self, cutoff_ms: int) -> int:
        with self._lock:
            if self._closed:
                return 0
            n = self._conn.execute("DELETE FROM trades WHERE ts < ?", (cutoff_ms,)).rowcount
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
