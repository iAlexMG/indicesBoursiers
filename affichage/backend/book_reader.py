"""Lecteur ASYNCHRONE de l'historique de carnet (books.db) pour la heatmap.

Porté de `crypto/affichage/gui/book_reader.py`. Deux garde-fous contre le gel quand books.db
devient grosse (sessions longues × plusieurs sources) :

1. **LECTURE BORNÉE** : on ne demande jamais une plage brute, mais ~`max_cols` colonnes
   réparties (`BookArchive.query_sampled`). La heatmap n'en affiche pas davantage de toute
   façon — sortir plus serait du travail jeté. Le coût dépend de la PLAGE demandée, pas de la
   taille de la base.
2. **HORS THREAD Qt** : la requête SQLite tourne dans un thread de fond. Le thread d'affichage
   ne bloque jamais ; il récupère le dernier résultat prêt, ou rien le temps que ça charge —
   la heatmap se complète une fraction de seconde plus tard, sans figer la fenêtre.

Le résultat est rendu au format d'`orderflow_data.FlowStore` — `(ts_s, bids, asks)` avec des
listes de `(prix, taille)` — pour être fusionnable avec la mémoire et directement consommable
par `FlowPanel`, qui n'a donc rien à savoir de l'existence du disque.
"""
from __future__ import annotations

import logging
import threading

log = logging.getLogger("book_reader")

# Plafond de colonnes rendues : au-delà, la heatmap n'a plus un pixel par colonne.
MAX_RENDER_COLS = 1000

Key = tuple   # (source, symbol, t0_ms, t1_ms) arrondi à la seconde


class BookReader:
    def __init__(self, archive, max_cols: int = MAX_RENDER_COLS,
                 cache_size: int = 8) -> None:
        self.archive = archive
        self.max_cols = max_cols
        self.cache_size = cache_size
        self._want: Key | None = None
        self._result: dict[Key, list] = {}
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stop = False
        self._thread = threading.Thread(target=self._run, name="book_reader", daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        # Joindre AVANT la fermeture de l'archive, sinon le thread peut lire une connexion
        # déjà fermée pendant l'arrêt.
        self._stop = True
        self._wake.set()
        self._thread.join(timeout=2.0)

    @staticmethod
    def key(source: str, symbol: str, t0_ms: int, t1_ms: int) -> Key:
        # Arrondi à la seconde : sans ça, chaque micro-variation de la fenêtre (chaque image)
        # relancerait une lecture disque.
        return (source, symbol, t0_ms // 1000 * 1000, t1_ms // 1000 * 1000)

    def request(self, key: Key) -> None:
        """Demande NON BLOQUANTE du chargement d'une fenêtre. Ignorée si déjà en cache ou
        déjà demandée."""
        with self._lock:
            if key == self._want or key in self._result:
                return
            self._want = key
        self._wake.set()

    def get(self, key: Key) -> list:
        """Le résultat s'il est prêt, sinon une liste vide — sans jamais bloquer."""
        with self._lock:
            return self._result.get(key, [])

    def _run(self) -> None:
        while not self._stop:
            self._wake.wait()
            self._wake.clear()
            if self._stop:
                break
            with self._lock:
                key = self._want
            if key is None or key in self._result:
                continue
            source, symbol, t0_ms, t1_ms = key
            try:
                snaps = self.archive.query_sampled(source, symbol, t0_ms, t1_ms, self.max_cols)
            except Exception as exc:  # noqa: BLE001 — I/O disque : résultat vide, pas de crash
                log.warning("lecture de carnet %s %s : %s", source, symbol, exc)
                snaps = []
            cols = [_to_store_format(s) for s in snaps]
            with self._lock:
                self._result[key] = cols
                while len(self._result) > self.cache_size:      # cache borné
                    self._result.pop(next(iter(self._result)))
            self._wake.set()   # une autre fenêtre a pu être demandée entre-temps


def _to_store_format(snap) -> tuple:
    """`BookSnapshot` (prix/tailles à plat) -> `(ts_s, bids, asks)` du FlowStore.

    L'archive stocke les niveaux à plat, bids puis asks, en perdant la frontière. On la
    reconstruit par le prix : un niveau <= best bid est un bid, >= best ask est un ask. C'est
    fiable parce que `insert_snapshot` enregistre le best bid et le best ask du moment.
    """
    bids, asks = [], []
    for p, s in zip(snap.prices.tolist(), snap.sizes.tolist()):
        if snap.bid and p <= snap.bid:
            bids.append((p, s))
        elif snap.ask and p >= snap.ask:
            asks.append((p, s))
    bids.sort(key=lambda x: -x[0])   # meilleur en premier : le contrat de FlowStore
    asks.sort(key=lambda x: x[0])
    return (snap.ts / 1000.0, bids, asks)
