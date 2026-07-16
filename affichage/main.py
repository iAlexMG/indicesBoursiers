"""Point d'entrée : monte les 3 accès au marché EN PARALLÈLE et lance le tableau de bord.

Les trois accès écrivent EN CONTINU, chacun dans ses propres `FlowStore` (clés
`(accès, symbole)`). Le menu « Accès » de la barre ne change que la source LUE : il ne
démarre ni n'arrête rien. C'est l'architecture du frère crypto (`gui/app.py` capte tous
les flux, `_apply_feed` se contente de rebrancher la vue), et c'est ce qui évite le trou
de données qu'une bascule à la demande creuserait à chaque changement.

  - "quantower" : le pont `NqFeed` (Rithmic). Carnet L2 complet, côté agresseur FOURNI par
                  le marché (couverture mesurée : 100 %) -> le footprint est exact.
  - "ibkr"      : TWS / IB Gateway. DÉGRADÉ : sans abonnement CME L2, heatmap et DOM
                  restent vides et le côté agresseur doit être INFÉRÉ (règle du tick).
                  Conservé pour ce qu'il démontre : deux accès au même marché.
  - "demo"      : générateur synthétique, aucune connexion.

Chaque source vit dans son propre thread et ne fait qu'écrire dans les stores ; le GUI les
relit sur son QTimer. C'est ce qui rend le binding Qt indifférent à la source — et en
particulier ce qui dissout le conflit apparent entre ib_insync et PySide6 : `util.useQt()`
(qui ne connaît pas PySide6) n'est JAMAIS appelé, puisque la boucle asyncio d'IB tourne
dans son thread à elle et n'a rien à intégrer.

AUCUN accès n'est obligatoire : un pont fermé, un TWS éteint ou un `ib_insync` absent
laissent leur vue vide sans gêner les autres.

Lancement :  python main.py
"""

import logging
import os
import sys
import threading

from pyqtgraph.Qt import QtWidgets

import config
from backend.book_archive import BookArchive
from backend.book_reader import BookReader
from backend.recorder import Recorder
from backend.trade_archive import TradeArchive
from logsetup import setup_logging
from orderflow_data import FlowStore
from ui import MainWindow

log = logging.getLogger("main")


class Storage:
    """Les deux archives, l'écrivain différé et le lecteur asynchrone, montés ensemble.

    Bases SÉPARÉES (`trades.db` / `books.db`) : volumes et cycles de vie très différents,
    l'une purgeable sans toucher l'autre — même découpage que le projet frère.
    """

    def __init__(self) -> None:
        os.makedirs(config.DATA_DIR, exist_ok=True)
        self.trades = TradeArchive(os.path.join(config.DATA_DIR, "trades.db"))
        self.books = BookArchive(os.path.join(config.DATA_DIR, "books.db"))
        self.recorder = Recorder(self.trades, self.books,
                                 retention_days=config.RETENTION_DAYS)
        self.recorder.start()
        self.reader = BookReader(self.books)
        self.reader.start()

    def store(self, symbol: str, source: str) -> FlowStore:
        return FlowStore(symbol, source=source, recorder=self.recorder,
                         book_reader=self.reader)

    def close(self) -> None:
        # ORDRE IMPORTANT : arrêter les threads AVANT de fermer les archives, sinon ils
        # liraient/écriraient sur une connexion SQLite déjà fermée.
        self.reader.stop()
        self.recorder.stop()
        self.trades.close()
        self.books.close()


class IbkrFeed(threading.Thread):
    """Accès IBKR : connexion TWS + souscriptions, dans SON thread et SA boucle asyncio.

    Le thread ne fait qu'écrire dans les `FlowStore` — il ne touche à aucun objet Qt, ce
    qui est la condition pour qu'ib_insync et PySide6 cohabitent sans `util.useQt()`.

    Tolérant à l'absence : ib_insync non installé, TWS éteint, mauvais port, pas de
    contrat -> on journalise et le thread s'arrête. Les autres accès continuent, et la
    vue IBKR reste simplement vide.
    """

    # UNE connexion TWS sert TOUS les symboles -> la clé d'état ne porte pas de symbole.
    key = ("ibkr", None)

    def __init__(self, stores: dict, log=print) -> None:
        super().__init__(name="ibkr-feed", daemon=True)
        self._stores = stores          # {symbol: FlowStore}
        self._log = log
        self._ib = None
        self._md = None                # cf. run() : DOIT survivre à la souscription
        self.connected = False
        self.last_error: str | None = None
        # ⚠️ PAS `self._stop` : `threading.Thread` a DÉJÀ une méthode interne `_stop()`,
        # que l'attribut écraserait -> `is_alive()` lève « 'Event' object is not callable »
        # et le thread ne se nettoie plus. Bug mesuré, pas théorique.
        self._stopping = threading.Event()

    def run(self) -> None:
        import asyncio

        # SA boucle, et AVANT d'importer ib_insync : `eventkit` (sa dépendance) appelle
        # `get_event_loop()` DÈS L'IMPORT du module, pas à la connexion. Importer d'abord
        # lève « There is no current event loop in thread 'ibkr-feed' ». Mesuré.
        # C'est aussi ce qui garantit qu'on ne touche jamais à la boucle du thread
        # principal, qui appartient à Qt.
        asyncio.set_event_loop(asyncio.new_event_loop())
        try:
            from ib_connection import connect, resolve_front_month
            from market_data import MarketDataManager
        except Exception as exc:  # noqa: BLE001 — ib_insync absent, ou refus à l'import
            # Celui-là ne se répare pas tout seul (contrairement à un TWS éteint) :
            # on renonce au lieu de boucler pour rien.
            self._log(f"indisponible (ib_insync : {exc})")
            return

        # Reconnexion à backoff plafonné, comme `QuantowerFeed` : TWS est une appli de
        # bureau qu'on lance souvent APRÈS le tableau de bord. Sans cette boucle, l'accès
        # IBKR mourait définitivement au démarrage et il fallait relancer l'app — ce qui
        # contredisait la promesse « les trois accès tournent en parallèle ».
        backoff = 1.0
        while not self._stopping.is_set():
            try:
                self._ib = connect()
                contracts = {sym: resolve_front_month(self._ib, sym)
                             for sym in self._stores}
                # ⚠️ GARDER le manager en vie : `pendingTickersEvent += self._on_pending_tickers`
                # ne le retient qu'en WEAKREF (eventkit connecte avec `keep_ref=False`). Un
                # `MarketDataManager(...).subscribe()` temporaire est collecté dès la fin de
                # l'instruction, son slot est retiré, et PLUS AUCUN TICK n'arrive — alors que
                # la connexion, elle, reste établie et que le journal a l'air parfaitement sain
                # (contrats résolus, pas une exception). Bug mesuré, pas théorique.
                self._md = MarketDataManager(self._ib, contracts, self._stores)
                self._md.subscribe()
                self.connected = True
                self.last_error = None
                self._log("connecté")
                backoff = 1.0
                # `IB.sleep` fait TOURNER la boucle d'événements pendant la durée demandée :
                # c'est ce qui distribue les ticks. Une boucle courte (contre `ib.run()`,
                # qui bloque sans fin) garde l'arrêt réactif.
                while not self._stopping.is_set() and self._ib.isConnected():
                    self._ib.sleep(0.2)
            except Exception as exc:  # noqa: BLE001 — un accès qui tombe ne casse rien
                self.last_error = str(exc)
                self._log(f"indisponible ({exc})")
            self.connected = False
            self._disconnect()
            if self._stopping.is_set():
                return
            self._log(f"nouvel essai dans {backoff:.0f} s")
            self._stopping.wait(backoff)
            backoff = min(backoff * 2, 30.0)

    @property
    def delayed(self) -> bool:
        """Le flux d'IBKR est-il différé ? (lu sur les tickers ; voir MarketDataManager)"""
        md = self._md
        return bool(md is not None and md.delayed)

    def _disconnect(self) -> None:
        try:
            if self._ib is not None and self._ib.isConnected():
                self._ib.disconnect()
        except Exception:  # noqa: BLE001
            pass

    def stop(self) -> None:
        self._stopping.set()
        self.join(timeout=3.0)


def _log_for(access: str):
    """Journal d'un accès. `QuantowerFeed`/`IbkrFeed` reçoivent un CALLABLE, pas un logger :
    ils restent ainsi utilisables seuls (le mode CLI de `quantower_feed.py` y passe `print`)."""
    return logging.getLogger(access).info


def build_quantower(storage: Storage, stores: dict) -> list:
    """Un client de pont PAR symbole, chacun sur le port de sa stratégie « NQ Feed ».

    Les clients se (re)connectent tout seuls en tâche de fond : on peut lancer l'app avant
    Quantower, fermer Quantower en cours de route, redémarrer une stratégie — le flux
    reprend sans relancer l'interface.
    """
    from quantower_feed import QuantowerFeed

    feeds = []
    qlog = logging.getLogger("quantower")
    for sym in config.SYMBOLS:
        stores[("quantower", sym)] = storage.store(sym, "quantower")
        port = config.QT_FEED_PORTS.get(sym)
        if port is None:
            qlog.warning("%s : aucun port de pont configuré (config.QT_FEED_PORTS) "
                         "— vue vide.", sym)
            continue
        feed = QuantowerFeed(stores[("quantower", sym)],
                             host=config.QT_FEED_HOST, port=port,
                             log=lambda m, s=sym: qlog.info("[%s] %s", s, m))
        # `key` sert à la ligne d'état de l'UI : elle y lit `connected`/`last_error` pour
        # dire POURQUOI une vue est vide, au lieu de laisser l'utilisateur deviner.
        feed.key = ("quantower", sym)
        feed.start()
        feeds.append(feed)
    return feeds


def build_ibkr(storage: Storage, stores: dict) -> list:
    """Accès IBKR : UN seul thread pour tous les symboles (une connexion TWS suffit)."""
    per_symbol = {}
    for sym in config.SYMBOLS:
        store = storage.store(sym, "ibkr")
        stores[("ibkr", sym)] = store
        per_symbol[sym] = store
    feed = IbkrFeed(per_symbol, log=_log_for("ibkr"))
    feed.start()
    return [feed]


def build_demo(storage: Storage, stores: dict) -> list:
    """Accès démo : PAS d'archive, volontairement.

    On n'écrit jamais de données SYNTHÉTIQUES dans les mêmes bases que le marché réel :
    une heatmap historique mi-vraie mi-inventée serait pire qu'aucune. Ces stores n'ont
    donc ni `recorder` ni `book_reader` — leur vue est bornée à la mémoire, et c'est tout
    ce qu'on veut d'un générateur.
    """
    from demo_feed import DemoFeed

    per_symbol = {sym: FlowStore(sym, source="demo") for sym in config.SYMBOLS}
    for sym, store in per_symbol.items():
        stores[("demo", sym)] = store
    demo = DemoFeed(per_symbol)
    demo.start()
    return [demo]


BUILDERS = {"quantower": build_quantower, "ibkr": build_ibkr, "demo": build_demo}


def main():
    path = setup_logging()
    log.info("=== démarrage du tableau de bord ; log -> %s ===", path)
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)

    storage = Storage()
    stores: dict = {}      # {(accès, symbole): FlowStore}
    feeds: list = []
    for access, _label in config.ACCESS:
        builder = BUILDERS.get(access)
        if builder is None:
            log.error("[%s] accès inconnu (config.ACCESS) — ignoré.", access)
            continue
        try:
            feeds += builder(storage, stores)
        except Exception as exc:  # noqa: BLE001 — un accès qui refuse de démarrer
            log.error("[%s] démarrage impossible (%s) — les autres continuent.",
                      access, exc)

    window = MainWindow(stores, feeds=feeds, storage=storage)
    window.showMaximized()
    app.exec()


if __name__ == "__main__":
    main()
