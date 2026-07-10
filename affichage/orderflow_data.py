"""Modèle de données + store en mémoire pour les graphiques orderflow.

Centralise tout ce que la vue (`flow_view.py`) consomme :
- `Trade`  : un trade exécuté (prix, taille, côté agresseur, timestamp).
- `Candle` : une bougie + son footprint (volume acheteur/vendeur par niveau de prix).
- `build_candles` : agrège des trades en bougies (porté du projet Crypto, `gui/candles.py`).
- `FlowStore` : ring buffers (trades + snapshots de carnet) alimentés soit par le flux
  IB réel (`market_data.py`), soit par le générateur démo (`demo_feed.py`).

Aucune dépendance externe (pas de SQLite/archives) : tout vit en mémoire, borné par
des fenêtres temporelles (`config.TRADES_WINDOW_SECONDS`, `config.BOOKS_WINDOW_SECONDS`).
"""
from __future__ import annotations

import math
import time
from collections import deque
from dataclasses import dataclass, field

import config


# --- Modèle ----------------------------------------------------------------

@dataclass(slots=True)
class Trade:
    """Un trade exécuté, normalisé. `ts` en epoch millisecondes."""
    price: float
    size: float
    side: str        # "buy" / "sell" = côté de l'agresseur
    ts: int          # epoch ms


@dataclass(slots=True)
class Candle:
    """Une bougie + son footprint (volume acheteur/vendeur par niveau de prix)."""
    t0: float                                   # début (s)
    t1: float                                   # fin (s)
    o: float
    h: float
    l: float
    c: float
    tick: float                                 # pas de regroupement des prix
    buy: dict = field(default_factory=dict)     # row -> volume acheteur
    sell: dict = field(default_factory=dict)    # row -> volume vendeur
    bid: float | None = None                    # best bid/ask reconstruits
    ask: float | None = None

    @property
    def buy_total(self) -> float:
        return sum(self.buy.values())

    @property
    def sell_total(self) -> float:
        return sum(self.sell.values())

    @property
    def delta(self) -> float:
        return self.buy_total - self.sell_total


def build_candles(trades, res_s: float, tick: float) -> list:
    """Agrège une liste de trades (triée par ts croissant) en bougies de `res_s`
    secondes, chaque prix regroupé par `tick`. Open = 1er trade, Close = dernier.

    Porté de Crypto `gui/candles.py::build_candles`.
    """
    if not trades or res_s <= 0 or tick <= 0:
        return []
    by_bucket: dict = {}
    for t in trades:
        ts = t.ts / 1000.0
        b0 = math.floor(ts / res_s) * res_s
        c = by_bucket.get(b0)
        if c is None:
            c = Candle(t0=b0, t1=b0 + res_s, o=t.price, h=t.price, l=t.price,
                       c=t.price, tick=tick)
            by_bucket[b0] = c
        else:
            if t.price > c.h:
                c.h = t.price
            if t.price < c.l:
                c.l = t.price
            c.c = t.price
        row = int(round(t.price / tick))
        # trades triés par ts croissant -> la dernière affectation par côté = le
        # dernier prix acheteur (ask) / vendeur (bid) de la bougie.
        if t.side == "buy":
            c.buy[row] = c.buy.get(row, 0.0) + t.size
            c.ask = t.price
        else:
            c.sell[row] = c.sell.get(row, 0.0) + t.size
            c.bid = t.price
    return [by_bucket[k] for k in sorted(by_bucket)]


# --- Store -----------------------------------------------------------------

def _is_valid(x) -> bool:
    return x is not None and not (isinstance(x, float) and math.isnan(x))


class FlowStore:
    """Buffers en mémoire pour un symbole : trades (avec côté+taille) et snapshots
    de carnet (pour la heatmap et l'échelle DOM). Bornés par fenêtre temporelle.

    Un snapshot de carnet = (ts_s, bids, asks) où bids/asks sont des listes de
    tuples (price, size) déjà triées (meilleur niveau en premier).
    """

    def __init__(self, symbol: str) -> None:
        self.symbol = symbol
        self.trades: deque = deque()
        self.books: deque = deque()
        self._last_price: float | None = None     # pour la règle up/down-tick
        self._last_snap_ms = 0                     # throttle des snapshots

    # -- écriture (alimenté par market_data / demo_feed) -------------------
    def add_trade(self, price, size, side=None, ts_ms=None,
                  bid=None, ask=None) -> None:
        """Ajoute un trade. Si `side` est None, il est inféré (règle du tick)."""
        if not _is_valid(price) or not _is_valid(size) or size <= 0:
            return
        ts_ms = int(ts_ms if ts_ms is not None else time.time() * 1000)
        if side is None:
            side = self._infer_side(price, bid, ask)
        self._last_price = price
        self.trades.append(Trade(price=float(price), size=float(size),
                                 side=side, ts=ts_ms))
        self._prune_trades(ts_ms / 1000.0)

    def add_book(self, bids, asks, ts_ms=None) -> None:
        """Enregistre un snapshot de carnet (throttle à `config.SNAPSHOT_MS`).

        `bids`/`asks` : listes de tuples (price, size) ou d'objets DOMLevel.
        """
        ts_ms = int(ts_ms if ts_ms is not None else time.time() * 1000)
        if ts_ms - self._last_snap_ms < config.SNAPSHOT_MS:
            return
        self._last_snap_ms = ts_ms
        b = _levels(bids)
        a = _levels(asks)
        if not b and not a:
            return
        self.books.append((ts_ms / 1000.0, b, a))
        self._prune_books(ts_ms / 1000.0)

    def _infer_side(self, price, bid, ask) -> str:
        """Côté agresseur : >= ask -> buy, <= bid -> sell, sinon up/down-tick."""
        if _is_valid(ask) and price >= ask:
            return "buy"
        if _is_valid(bid) and price <= bid:
            return "sell"
        if self._last_price is not None:
            return "buy" if price >= self._last_price else "sell"
        return "buy"

    def _prune_trades(self, now_s: float) -> None:
        cutoff = now_s - config.TRADES_WINDOW_SECONDS
        tr = self.trades
        while tr and tr[0].ts / 1000.0 < cutoff:
            tr.popleft()

    def _prune_books(self, now_s: float) -> None:
        cutoff = now_s - config.BOOKS_WINDOW_SECONDS
        bk = self.books
        while bk and bk[0][0] < cutoff:
            bk.popleft()

    # -- lecture (consommé par flow_view) ---------------------------------
    def visible_trades(self, t0: float, t1: float) -> list:
        return [t for t in self.trades if t0 <= t.ts / 1000.0 <= t1]

    def visible_books(self, t0: float, t1: float) -> list:
        return [s for s in self.books if t0 <= s[0] <= t1]

    def last_book(self):
        """Dernier snapshot (ts_s, bids, asks) ou None."""
        return self.books[-1] if self.books else None

    @property
    def t_last(self) -> float | None:
        """Timestamp (s) de la dernière activité (trade ou carnet)."""
        t = self.trades[-1].ts / 1000.0 if self.trades else None
        b = self.books[-1][0] if self.books else None
        if t is None:
            return b
        if b is None:
            return t
        return max(t, b)


def _levels(side) -> list:
    """Normalise une liste de niveaux (tuples (price,size) OU objets DOMLevel) en
    liste de tuples (price, size) valides."""
    out = []
    for lv in side or ():
        if lv is None:
            continue
        if isinstance(lv, (tuple, list)):
            price, size = lv[0], lv[1]
        else:  # DOMLevel d'ib_insync : attributs .price / .size
            price, size = getattr(lv, "price", None), getattr(lv, "size", None)
        if _is_valid(price) and _is_valid(size) and size > 0:
            out.append((float(price), float(size)))
    return out
