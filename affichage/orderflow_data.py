"""Modèle de données + store en mémoire pour les graphiques orderflow.

Centralise tout ce que la vue (`flow_view.py`) consomme :
- `Trade`  : un trade exécuté (prix, taille, côté agresseur, timestamp).
- `Candle` : une bougie + son footprint (volume acheteur/vendeur par niveau de prix).
- `build_candles` : agrège des trades en bougies (porté du projet Crypto, `gui/candles.py`).
- `FlowStore` : ring buffers (trades + snapshots de carnet) alimentés par l'un des trois
  accès — le pont Quantower/Rithmic (`quantower_feed.py`), IBKR (`market_data.py`) ou le
  générateur démo (`demo_feed.py`). Un store par `(accès, symbole)`.

Les buffers sont bornés par fenêtre temporelle (`config.TRADES_WINDOW_SECONDS`,
`config.BOOKS_WINDOW_SECONDS`). Au-delà, `visible_books` complète la mémoire par le DISQUE
(`backend/`) — c'est le SEUL point où l'archive entre dans la vue, et `FlowPanel` ignore
qu'elle existe. `visible_trades`, lui, ne lit jamais le disque : voir sa docstring.
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

    def __init__(self, symbol: str, source: str = "live",
                 recorder=None, book_reader=None) -> None:
        self.symbol = symbol
        self.source = source          # "quantower" / "ibkr" / "demo" : la clé du flux
        self.trades: deque = deque()
        self.books: deque = deque()
        self._recorder = recorder     # écriture disque différée (peut être None)
        self._reader = book_reader    # relecture disque asynchrone (peut être None)
        self._last_price: float | None = None     # pour la règle up/down-tick
        self._last_snap_ms = 0                     # throttle des snapshots (affichage)
        self._last_rec_ms = 0                      # throttle de l'ENREGISTREMENT (disque)
        # Dernier instant où le CONTENU du carnet a réellement CHANGÉ. À ne pas confondre
        # avec l'arrivée d'un snapshot : le pont photographie toutes les 250 ms qu'il se
        # passe quelque chose ou non. Quand Rithmic cesse d'alimenter Quantower, les photos
        # continuent d'arriver, fraîches et IDENTIQUES — `t_last` reste au présent et l'app
        # se croit vivante alors qu'elle peint un mort. Mesuré : 80 photos, 1 seule valeur
        # distincte, 0 trade, en pleine séance. C'est CE champ qui permet de le dire.
        self.t_book_change: float | None = None

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
        if self._recorder is not None:
            self._recorder.trade(self.source, self.symbol, ts_ms,
                                 float(price), float(size), side)
        self._prune_trades(ts_ms / 1000.0)

    def add_book(self, bids, asks, ts_ms=None, throttle=True) -> None:
        """Enregistre un snapshot de carnet (throttle à `config.SNAPSHOT_MS`).

        `bids`/`asks` : listes de tuples (price, size) ou d'objets DOMLevel.

        `throttle=False` pour une source qui échantillonne DÉJÀ à la bonne cadence (le pont
        Quantower, qui photographie le carnet toutes les `SnapshotMs` côté C#). Sans ce
        drapeau, les deux throttles se superposent au même seuil et la gigue réseau en fait
        tomber une bonne part : mesuré à **29 % de snapshots perdus** en silence, un pont à
        250 ms contre un `SNAPSHOT_MS` à 250 ms. Le flux IBKR, lui, pousse à chaque tick et a
        bel et bien besoin du throttle — d'où le défaut à True.
        """
        ts_ms = int(ts_ms if ts_ms is not None else time.time() * 1000)
        if throttle and ts_ms - self._last_snap_ms < config.SNAPSHOT_MS:
            return
        self._last_snap_ms = ts_ms
        b = _levels(bids)
        a = _levels(asks)
        if not b and not a:
            return
        # Le contenu a-t-il bougé depuis la photo précédente ? (comparaison avant l'ajout)
        prev = self.books[-1] if self.books else None
        if prev is None or prev[1] != b or prev[2] != a:
            self.t_book_change = ts_ms / 1000.0
        self.books.append((ts_ms / 1000.0, b, a))
        # L'ENREGISTREMENT a sa propre cadence, plus lente que l'affichage : cf.
        # config.RECORD_SNAPSHOT_MS. Sans ce second throttle, la base prendrait 1,54 Go
        # PAR JOUR (mesuré) pour une finesse que la heatmap historique n'affiche jamais.
        if self._recorder is not None and ts_ms - self._last_rec_ms >= config.RECORD_SNAPSHOT_MS:
            self._last_rec_ms = ts_ms
            self._recorder.book(self.source, self.symbol, ts_ms, b, a)
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
        """Trades de la fenêtre — MÉMOIRE SEULE, volontairement.

        Contrairement au carnet, on ne complète pas par le disque ici. À ~9 trades/s
        (mesuré), une journée pèse ~750 000 trades : les charger à chaque image pour
        reconstruire le footprint coûterait O(trades) et gèlerait l'affichage — c'est
        exactement le mur que crypto a rencontré (~300 ms par image, GIL saturé) et
        contourné par un ROLLUP pré-agrégé, pas par une lecture brute.

        Tant qu'aucun rollup n'existe ici, le footprint reste borné à la fenêtre mémoire.
        Le seuil de bascule est à MESURER avant de porter le rollup du frère : sur un seul
        instrument, la limite est peut-être bien plus loin que chez lui. La heatmap, elle,
        peut remonter loin sans risque parce que sa lecture est bornée par l'AFFICHAGE
        (~1000 colonnes), pas par le volume de données.
        """
        return [t for t in self.trades if t0 <= t.ts / 1000.0 <= t1]

    def visible_books(self, t0: float, t1: float) -> list:
        """Snapshots de la fenêtre : la MÉMOIRE, complétée par le DISQUE en deçà.

        La mémoire ne garde que `config.BOOKS_WINDOW_SECONDS` (1 h). Quand la fenêtre
        demandée remonte plus loin, on réclame le reste à `BookReader` — qui lit hors du
        thread Qt et renvoie vide tant que ce n'est pas prêt. **Cet appel ne bloque JAMAIS** :
        la heatmap se complète à l'image suivante plutôt que de figer la fenêtre.

        C'est ici que le disque entre dans la vue, et nulle part ailleurs : `FlowPanel`
        continue d'appeler `visible_books` sans savoir qu'une archive existe.
        """
        mem = [s for s in self.books if t0 <= s[0] <= t1]
        if self._reader is None:
            return mem
        # Bord de la mémoire : au-delà (vers le passé), c'est au disque de répondre.
        oldest_mem = mem[0][0] if mem else (self.books[0][0] if self.books else t1)
        if t0 >= oldest_mem - 1.0:
            return mem
        key = self._reader.key(self.source, self.symbol,
                               int(t0 * 1000), int(min(oldest_mem, t1) * 1000))
        self._reader.request(key)
        disk = [s for s in self._reader.get(key) if t0 <= s[0] < oldest_mem]
        return disk + mem

    def last_book(self):
        """Dernier snapshot (ts_s, bids, asks) ou None."""
        return self.books[-1] if self.books else None

    def frozen_for(self, now_s: float | None = None) -> float | None:
        """Depuis combien de secondes le carnet est-il FIGÉ, alors que les photos arrivent ?

        `None` quand la question n'a pas de sens : aucun carnet reçu (accès sans L2, comme
        IBKR sans abonnement — là c'est l'ABSENCE qu'il faut dire, pas le gel).

        ⚠️ Ne PAS confondre avec `t_last`, qui ne mesure que l'ARRIVÉE : un pont dont la
        source est morte continue de livrer des photos fraîches et identiques, et `t_last`
        répond « il y a 0,2 s » sur un carnet mort depuis 20 minutes.
        """
        if not self.books or self.t_book_change is None:
            return None
        return (now_s if now_s is not None else time.time()) - self.t_book_change

    @property
    def t_last(self) -> float | None:
        """Timestamp (s) de la dernière activité (trade ou carnet).

        ⚠️ C'est l'heure d'ARRIVÉE, pas une preuve de vie : voir `frozen_for`.
        """
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
