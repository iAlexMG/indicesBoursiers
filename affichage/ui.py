"""Fenêtre principale : une barre de commandes + UN graphe (style projet crypto).

Calquée sur `crypto/affichage/gui/app.py` : un seul `FlowPanel`, dont on change le symbole
et l'accès par des BOUTONS et un menu, au lieu d'un onglet par symbole. La différence n'est
pas cosmétique — les stores des trois accès × deux symboles sont TOUS alimentés en
permanence (main.py), donc la bascule ne fait que rebrancher la vue sur une autre clé :
elle est instantanée et le flux qu'on quitte ne se fige pas.

Sont volontairement absents de la barre du frère :
  - `Dislocation ⚙` : SANS OBJET ici (un seul marché, donc pas d'arbitrage entre places) ;
  - `Marché` Futures/Spot : SANS OBJET (futures CME seuls).
La résolution GARDE l'échelle 1s→5m propre aux indices (crypto va de 1m à 1j).
"""
from datetime import datetime
from zoneinfo import ZoneInfo

from pyqtgraph.Qt import QtCore, QtWidgets

import config
from controls import LayersPanel
from flow_view import FlowPanel
from orderflow_data import FlowStore

TZ = ZoneInfo(config.TIMEZONE)

# résolutions du footprint (libellé -> secondes). On garde l'échelle SOUS la minute :
# c'est le sens de ce tableau de bord (orderflow), là où le frère crypto part de 1m.
RESOLUTIONS = [("1s", 1), ("5s", 5), ("15s", 15), ("30s", 30), ("1m", 60), ("5m", 300)]

_BTN_CSS = (
    "QPushButton{padding:6px 16px;font-weight:600;color:#8b949e;"
    "background:#1c222b;border:1px solid #2b3340;}"
    "QPushButton:checked{background:#58a6ff;color:#06101f;}"
)
_LBL_CSS = "color:#8b949e;font-weight:600;"
_COMBO_CSS = (
    "QComboBox{padding:4px 8px;color:#c9d1d9;background:#1c222b;"
    "border:1px solid #2b3340;}"
    "QComboBox QAbstractItemView{background:#1c222b;color:#c9d1d9;"
    "selection-background-color:#58a6ff;selection-color:#06101f;}"
)


class MainWindow(QtWidgets.QMainWindow):
    """Barre de commandes + un `FlowPanel`, rafraîchis par un QTimer."""

    def __init__(self, stores, feeds=None, storage=None):
        super().__init__()
        self.setWindowTitle("Indices — Orderflow / Footprint / DOM (CME)")

        self.stores = stores               # {(accès, symbole): FlowStore}
        self.feeds = feeds or []
        self.storage = storage
        # Flux indexés par clé d'état, pour que la ligne d'état puisse dire POURQUOI une vue
        # est vide. Un flux sans `key` (ex. le générateur démo) n'a rien à expliquer.
        # Clé `(accès, None)` = un flux qui sert TOUS les symboles (le thread IBKR).
        self._feed_by_key = {f.key: f for f in self.feeds if getattr(f, "key", None)}
        self.symbol = config.SYMBOLS[0]
        self.access = config.SOURCE if any(a == config.SOURCE for a, _ in config.ACCESS) \
            else config.ACCESS[0][0]

        # graphe UNIQUE, créé avant la barre : les commandes s'y connectent.
        self.view = FlowPanel(self.symbol, self._store())

        bar = QtWidgets.QHBoxLayout()

        rlbl = QtWidgets.QLabel("Résolution")
        rlbl.setStyleSheet(_LBL_CSS)
        bar.addWidget(rlbl)
        self._res = QtWidgets.QComboBox()
        for lab, sec in RESOLUTIONS:
            self._res.addItem(lab, sec)
        start = next((i for i, (_, s) in enumerate(RESOLUTIONS)
                      if s == config.RESOLUTION_SECONDS), 1)
        self._res.setCurrentIndex(start)
        self._res.setStyleSheet(_COMBO_CSS)
        self._res.currentIndexChanged.connect(self._change_resolution)
        bar.addWidget(self._res)
        self.view.set_resolution(float(self._res.currentData()))

        bar.addStretch(1)

        # saut à une date précise. N'a de sens que depuis la phase 4 : c'est books.db qui
        # répond au-delà de l'heure gardée en mémoire.
        dlbl = QtWidgets.QLabel("Aller au")
        dlbl.setStyleSheet(_LBL_CSS)
        bar.addWidget(dlbl)
        self._date = QtWidgets.QDateTimeEdit(QtCore.QDateTime.currentDateTime())
        self._date.setDisplayFormat("yyyy-MM-dd HH:mm")
        self._date.setCalendarPopup(True)
        self._date.setStyleSheet("QDateTimeEdit{padding:4px 6px;color:#c9d1d9;"
                                 "background:#1c222b;border:1px solid #2b3340;}")
        bar.addWidget(self._date)
        gobtn = QtWidgets.QPushButton("Aller")
        gobtn.setStyleSheet(_BTN_CSS)
        gobtn.clicked.connect(self._goto_date)
        bar.addWidget(gobtn)
        bar.addSpacing(12)

        # --- Accès : Quantower / IBKR / Démo. Les trois flux tournent déjà : ce menu ne
        #     change QUE la clé lue (analogue du menu « Exchange » du frère). ---
        self._acc = QtWidgets.QComboBox()
        for key, label in config.ACCESS:
            self._acc.addItem(label, key)
        self._acc.setCurrentIndex(next(i for i, (k, _) in enumerate(config.ACCESS)
                                       if k == self.access))
        self._acc.setStyleSheet(_COMBO_CSS)
        self._acc.setToolTip("Source lue. Les trois accès sont captés en continu :\n"
                             "basculer ne fige ni ne redémarre rien.")
        self._acc.currentIndexChanged.connect(
            lambda _=0: self._switch_access(self._acc.currentData()))
        bar.addWidget(self._acc)
        bar.addSpacing(10)

        # --- Affichage : réglages groupés des couches (popup) ---
        self._layers_panel = LayersPanel(self.view)
        self._btn_layers = QtWidgets.QPushButton("Affichage ⚙")
        self._btn_layers.setStyleSheet(_BTN_CSS)
        self._btn_layers.clicked.connect(
            lambda: self._layers_panel.popup_under(self._btn_layers))
        bar.addWidget(self._btn_layers)

        # mode d'affichage : un seul bouton Live (bascule). Glisser = quitte Live.
        bar.addSpacing(10)
        self._btn_live = QtWidgets.QPushButton("● Live")
        self._btn_live.setCheckable(True)
        self._btn_live.setChecked(True)
        self._btn_live.setStyleSheet(_BTN_CSS)
        self._btn_live.clicked.connect(self._go_live)
        bar.addWidget(self._btn_live)

        self._group = QtWidgets.QButtonGroup(self)
        for sym in config.SYMBOLS:
            btn = QtWidgets.QPushButton(sym)
            btn.setCheckable(True)
            btn.setChecked(sym == self.symbol)
            btn.clicked.connect(lambda _=False, s=sym: self._switch_symbol(s))
            btn.setStyleSheet(_BTN_CSS)
            self._group.addButton(btn)
            bar.addWidget(btn)

        # Horloge du fuseau d'AFFICHAGE (config.TIMEZONE) — le même que l'axe des temps.
        # Sur un tableau de bord de marché, l'heure n'est pas une décoration : elle situe la
        # séance (CME régulière 09:30-16:00 ET, coupure 17:00-18:00) et donne l'échelle de
        # tout ce qui est à l'écran.
        bar.addSpacing(12)
        self._clock = QtWidgets.QLabel("")
        self._clock.setStyleSheet("color:#c9d1d9;font-weight:600;font-size:13px;"
                                  "font-family:Consolas,monospace;")
        self._clock.setToolTip(f"Heure locale — {config.TIMEZONE}")
        bar.addWidget(self._clock)

        quit_btn = QtWidgets.QPushButton("✕ Quitter")
        quit_btn.setStyleSheet("QPushButton{padding:6px 14px;font-weight:600;"
                               "color:#f0b0b0;background:#2a1c1f;border:1px solid #5a2b30;}")
        quit_btn.clicked.connect(self.close)
        bar.addSpacing(10)
        bar.addWidget(quit_btn)

        # Ligne d'état : dit ce que l'accès courant donne, et sinon POURQUOI il ne donne
        # rien. Sans elle, un pont fermé produit un écran noir muet — indiscernable d'un
        # bug, et c'est exactement ce qui est arrivé.
        self.status = QtWidgets.QLabel("")
        self.status.setStyleSheet("color:#8b949e;font-size:12px;padding:2px 2px;")
        self.status.setFixedHeight(20)

        central = QtWidgets.QWidget()
        root = QtWidgets.QVBoxLayout(central)
        root.setContentsMargins(10, 8, 10, 8)
        root.addLayout(bar)
        root.addWidget(self.status)
        root.addWidget(self.view, 1)
        self.setCentralWidget(central)
        self.setStyleSheet("background:#0d1117;")

        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self.refresh)
        self.timer.start(config.UPDATE_INTERVAL_MS)

    def _store(self):
        """Le store de la clé courante, jamais None.

        Si l'accès n'a pas été monté (son builder a échoué au démarrage), on fabrique un
        store VIDE plutôt que de laisser la vue sur le flux précédent : afficher les
        chiffres d'un accès sous l'étiquette d'un autre serait un mensonge, et c'est
        précisément ce que ce tableau de bord est censé démontrer — d'où vient la donnée.
        """
        key = (self.access, self.symbol)
        store = self.stores.get(key)
        if store is None:
            store = FlowStore(self.symbol, source=self.access)
            self.stores[key] = store
        return store

    def _rebind(self) -> None:
        self.view.set_symbol(self.symbol, self._store())

    def _switch_symbol(self, symbol: str) -> None:
        self.symbol = symbol
        self._rebind()

    def _switch_access(self, access: str) -> None:
        if access and access != self.access:
            self.access = access
            self._rebind()

    def _change_resolution(self) -> None:
        self.view.set_resolution(float(self._res.currentData()))

    def _goto_date(self) -> None:
        dt = self._date.dateTime().toPython().replace(tzinfo=TZ)
        self.view.goto(dt.timestamp())

    def _go_live(self) -> None:
        self.view.go_live()
        self._btn_live.setChecked(True)

    @staticmethod
    def _cme_pause() -> str | None:
        """Le CME est-il fermé en ce moment ? Libellé de la pause, ou None si ouvert.

        Séance : dimanche 18:00 ET → vendredi 17:00 ET, coupure quotidienne 17:00-18:00 ET.
        `config.TIMEZONE` est déjà l'heure de l'Est — c'est le fuseau de l'axe et de l'horloge.

        Sans ça, la coupure de 17 h ressemble à une panne : zéro trade partout, donc ni
        footprint, ni points, ni bougies — un écran presque vide, SANS explication depuis que
        la ligne d'état se tait quand tout va bien. Vécu le 2026-07-16 à 17:13 : « plus rien
        ne s'affiche », alors que les deux ponts servaient des carnets pleins et que le seul
        coupable était l'horaire. Un vide légitime doit se dire.
        """
        now = datetime.now(TZ)
        wd, hm = now.weekday(), now.hour + now.minute / 60.0
        if (wd == 4 and hm >= 17.0) or wd == 5 or (wd == 6 and hm < 18.0):
            return "CME fermé (week-end) — réouverture dimanche 18:00 ET"
        if 17.0 <= hm < 18.0:
            return "coupure quotidienne CME (17:00-18:00 ET)"
        return None

    def _status_text(self) -> str:
        """Ce que l'accès courant donne — ou pourquoi il ne donne rien."""
        label = dict(config.ACCESS).get(self.access, self.access)
        store = self.view.store
        t = store.t_last
        # Le flux de l'accès courant (clé par symbole, sinon la clé « tous symboles » d'IBKR).
        feed = (self._feed_by_key.get((self.access, self.symbol))
                or self._feed_by_key.get((self.access, None)))
        if t:
            # Quand tout va bien, la ligne d'état se TAIT : « reçu il y a 0,2 s · 27 trades
            # en mémoire » ne disait rien que le graphe ne montre déjà, et un bandeau
            # permanent finit par ne plus se lire — ce qui est exactement ce qu'on ne veut
            # pas le jour où il annonce une panne. Elle ne parle donc que dans l'anomalie.
            # (Retrait demandé par l'utilisateur, 2026-07-16.)
            #
            pause = self._cme_pause()
            # Des photos fraîches ne prouvent PAS que la donnée vit : quand la source du pont
            # meurt, il continue de photographier un carnet figé. C'est le CONTENU qui tranche.
            # ⚠️ Marché fermé, le gel est ATTENDU (le week-end, rien ne bouge nulle part) :
            # crier « FLUX GELÉ » un samedi serait une fausse alerte, et une fausse alerte
            # coûte la confiance qu'on essaie de bâtir.
            gel = store.frozen_for()
            if pause is None and gel is not None and gel > config.STALE_BOOK_SECONDS:
                return (f"⚠  {label} · {self.symbol} — FLUX GELÉ : le carnet n'a pas bougé "
                        f"depuis {gel:.0f} s (les photos arrivent toujours). "
                        f"Rithmic est-il encore connecté dans Quantower ?")
            if pause:
                return (f"◌  {pause} — aucune transaction ne circule : footprint et trades "
                        f"vides, c'est attendu. Le carnet, lui, continue d'évoluer.")
            return ""

        # Vue vide : on cherche la raison auprès du flux.
        err = getattr(feed, "last_error", None) if feed is not None else None
        # Connecté mais rien encore : ce n'est PAS une panne, c'est de la patience. IBKR met
        # plusieurs secondes à résoudre ses contrats avant le premier tick (mesuré : rien
        # après 8 s alors que la connexion était établie). Le dire, sinon l'utilisateur
        # cherche un problème qui n'existe pas.
        if getattr(feed, "connected", False):
            # « En attente des premiers ticks » mentirait un samedi : il n'en viendra aucun
            # avant dimanche 18:00 ET. Dire l'horaire plutôt que faire espérer.
            pause = self._cme_pause()
            if pause:
                return f"◌  {label} · {self.symbol} — connecté, mais {pause}."
            return (f"◌  {label} · {self.symbol} — connecté, en attente des premiers ticks…")
        if self.access == "quantower" and err:
            port = config.QT_FEED_PORTS.get(self.symbol)
            return (f"○  {label} · {self.symbol} — aucune donnée. Le pont ne répond pas sur "
                    f"{config.QT_FEED_HOST}:{port} — la stratégie « NQ-ES RealTime » tourne-t-elle "
                    f"dans Quantower, en Working ?")
        if self.access == "ibkr" and err:
            return (f"○  {label} · {self.symbol} — aucune donnée. TWS / IB Gateway ne répond "
                    f"pas sur {config.HOST}:{config.PORT}.")
        if err:
            return f"○  {label} · {self.symbol} — aucune donnée ({err})"
        return (f"○  {label} · {self.symbol} — aucune donnée pour l'instant "
                f"(voir logs/indices.log)")

    def _notice_for(self) -> tuple:
        """Le bandeau à poser SUR les graphes : (texte principal, texte du DOM).

        Dit ce qu'une couche vide ne peut pas dire d'elle-même. La vue ne voit qu'un store ;
        seule la fenêtre sait de quel accès il vient et ce que cet accès ne donne pas.
        """
        store = self.view.store
        if self.access == "ibkr":
            feed = self._feed_by_key.get(("ibkr", None))
            # Si TWS ne répond pas, la ligne d'état le dit déjà : ne pas accuser l'abonnement
            # d'une panne de connexion.
            if not (getattr(feed, "connected", False) or store.t_last):
                return (None, None)
            # Le carnet arrive ? Alors l'abonnement est là et il n'y a plus rien à expliquer.
            # Le bandeau se retire donc TOUT SEUL le jour où un abonnement est souscrit —
            # plutôt que de mentir en sens inverse.
            if store.books:
                return (None, None)
            # Idem pour le retard : LU sur `marketDataType` (ce que TWS répond), jamais
            # supposé depuis `config.MARKET_DATA_TYPE`, qui ne dit que « du différé si je
            # n'ai pas mieux ».
            retard = (" Les trades sont différés (~11,5 min mesuré) et incomplets "
                      "(~4 % des transactions)." if getattr(feed, "delayed", False)
                      else " Les trades sont incomplets (~4 % des transactions).")
            return ("IBKR — aucun abonnement CME : le carnet n'est pas servi, "
                    "heatmap et DOM restent vides." + retard,
                    "aucun\nabonnement")
        gel = store.frozen_for()
        if gel is not None and gel > config.STALE_BOOK_SECONDS:
            return (f"FLUX GELÉ — le carnet n'a pas bougé depuis {gel:.0f} s alors que les "
                    f"photos arrivent toujours. Rithmic n'alimente plus Quantower : "
                    f"vérifier la connexion dans la plateforme.", None)
        return (None, None)

    def refresh(self) -> None:
        self.view.refresh()
        self.view.set_notice(*self._notice_for())
        self.status.setText(self._status_text())
        self._clock.setText(datetime.now(TZ).strftime("%H:%M:%S"))
        # le bouton reflète l'état RÉEL : un glissement souris quitte le live tout seul.
        if self._btn_live.isChecked() != self.view.follow:
            self._btn_live.setChecked(self.view.follow)

    def closeEvent(self, event):  # noqa: N802 (API Qt)
        """Arrêt propre : couper le rendu, puis les sources, puis SEULEMENT le disque."""
        self.timer.stop()
        for feed in self.feeds:
            try:
                feed.stop()
            except Exception:  # noqa: BLE001
                pass
        # EN DERNIER, et seulement une fois les sources coupées : `Storage.close` vide la
        # file d'écriture puis ferme les bases. L'inverse perdrait les dernières lignes et
        # ferait écrire un thread sur une connexion morte.
        if self.storage is not None:
            try:
                self.storage.close()
            except Exception:  # noqa: BLE001
                pass
        event.accept()
