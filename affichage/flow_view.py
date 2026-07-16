"""Vue combinée orderflow + orderbook + footprint (style projet Crypto).

Un seul graphe par symbole superpose, du fond vers l'avant :
  - HEATMAP de liquidité (snapshots de carnet binnés dans le temps, type Bookmap),
  - lignes BEST BID / BEST ASK,
  - SCATTER des trades (taille ∝ volume, vert acheteur / rouge vendeur),
  - FOOTPRINT (chandelier discret + histogramme bid/ask par niveau, type Quantower).
À droite, une échelle DOM verticale (carnet courant) alignée sur la même grille de prix.

Porté/adapté de Crypto `gui/orderflow_view.py` et `gui/footprint_view.py`, sans aucune
dépendance au backend Crypto : la vue lit un `orderflow_data.FlowStore`.

Tout passe par `pyqtgraph.Qt`, qui résout le binding lui-même : ce module ne connaît ni
PySide6 ni PyQt5. C'est ce qui a rendu le passage à PySide6 (alignement sur le frère
crypto) gratuit — aucun `pyqtSignal`, aucun import direct du binding à traduire.
"""
from __future__ import annotations

import math
from datetime import datetime
from zoneinfo import ZoneInfo

import numpy as np
import pyqtgraph as pg
from pyqtgraph.Qt import QtCore, QtGui, QtWidgets

import config
from orderflow_data import build_candles

pg.setConfigOptions(antialias=True)

Qt = QtCore.Qt
QPointF, QRectF = QtCore.QPointF, QtCore.QRectF
QColor, QFont, QPen = QtGui.QColor, QtGui.QFont, QtGui.QPen

BUY = tuple(config.BUY)
SELL = tuple(config.SELL)
TZ = ZoneInfo(config.TIMEZONE)

# heatmap : sombre -> chaud
_HEAT = pg.ColorMap(
    [0.0, 0.35, 0.7, 1.0],
    [(8, 12, 22, 255), (20, 60, 110, 255), (70, 150, 180, 255), (250, 240, 150, 255)],
)

MAX_ROWS = config.FOOTPRINT_MAX_ROWS   # lignes max du footprint -> tick adaptatif (cf. config)
IMB_RATIO = 3.0          # imbalance : un côté >= 3x l'autre -> barre en surbrillance
CENTER_GAP = 0.03        # demi-espace central (mèche)
MIN_COL_PX = 70          # largeur écran mini d'une bougie pour afficher les chiffres
MIN_ROW_PX = 12          # hauteur écran mini d'une cellule pour afficher les chiffres
NBINS_MAX = 600          # garde-fou résolution heatmap
MAX_RENDER_COLS = 1000   # colonnes temps max de la heatmap (perf)
SAMPLE_HINT_S = 0.5      # pas de temps cible pour la grille heatmap
RIGHT_MARGIN_FRAC = 0.04
Y_EMA = 0.2              # lissage de l'axe Y auto
Y_PAD = 0.15
DOT_SCALE = 3.0          # pente de la taille des points selon √volume (forme de la courbe)
DOT_MIN_PX = 3.0         # diamètre mini d'un point
DOT_MAX_PX = 26.0        # diamètre maxi NOMINAL ; le réglage « Taille points » va jusqu'à 1,6×

# --- Filtre des trades affichés (réglages du popup « Trades ») ---------------
# Portés de crypto (gui/orderflow_view.py). Nécessaires ici aussi : à ~9 trades/s (mesuré
# sur NQ), l'heure gardée en mémoire par le FlowStore fait ~32 000 points — bien au-delà de
# ce que pyqtgraph dessine sans ramer. On n'affiche donc jamais tout.
MAX_POINTS = 1200        # cible de points (filtre Auto) à la plage de RÉFÉRENCE
MIN_POINTS = 60          # plancher de points sur très grande plage (épure)
AUTO_DECAY = 0.7         # plus grand = se resserre plus vite quand la plage s'élargit
HARD_CAP = 6000          # plafond DUR, même Auto désactivé -> le rendu reste fluide

GRID_PEN = pg.mkPen(QColor(46, 54, 66), width=1, cosmetic=True)
POC_PEN = pg.mkPen(QColor(232, 198, 92), width=1, cosmetic=True)
_TIME_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 86400]


def _fmt(v: float) -> str:
    if v >= 1000:
        return f"{v:,.0f}"
    if v >= 10:
        return f"{v:.0f}"
    if v >= 1:
        return f"{v:.1f}"
    return f"{v:.2f}"


# --- Axes & ViewBox --------------------------------------------------------

class TzDateAxis(pg.DateAxisItem):
    """Axe temps au fuseau configuré ; graduations calculées depuis la plage.

    `min_step` (secondes) = la RÉSOLUTION du footprint : jamais de graduation plus fine
    qu'une bougie. Sans elle, l'axe ne visait que « ~7 graduations » et pouvait poser une
    grille de 30 s sous des bougies de 1 min — elle découpait les chandelles en leur milieu
    et suggérait une précision que les données n'ont pas. Calé sur le frère crypto, où la
    grille suit la résolution. Le `showGrid` de pyqtgraph dessine SUR ces graduations : les
    régler ici règle aussi la grille.
    """

    min_step = 0.0

    def tickValues(self, minVal, maxVal, size):
        span = maxVal - minVal
        if span <= 0:
            return []
        # `or [...]` : garde-fou si la résolution dépasse tous les pas connus — mieux vaut
        # le pas le plus grossier qu'une liste vide (axe sans aucune graduation).
        steps = [s for s in _TIME_STEPS if s >= self.min_step] or [_TIME_STEPS[-1]]
        spacing = steps[-1]
        for s in steps:
            if span / s <= 7:
                spacing = s
                break
        first = math.ceil(minVal / spacing) * spacing
        ticks, v = [], first
        while v <= maxVal:
            ticks.append(v)
            v += spacing
        return [(spacing, ticks)]

    def tickStrings(self, values, scale, spacing):
        out = []
        for v in values:
            try:
                dt = datetime.fromtimestamp(v, TZ)
            except (OSError, ValueError, OverflowError):
                out.append("")
                continue
            if spacing >= 86400:
                out.append(dt.strftime("%m-%d"))
            elif spacing >= 60:
                out.append(dt.strftime("%H:%M"))
            else:
                out.append(dt.strftime("%H:%M:%S"))
        return out


class PriceAxis(pg.AxisItem):
    def tickStrings(self, values, scale, spacing):
        return [f"{v:,.2f}" if abs(v) < 1000 else f"{v:,.0f}" for v in values]


class FlowViewBox(pg.ViewBox):
    """Molette = zoom du TEMPS (ancré au présent en live) ; Ctrl+molette = zoom des PRIX."""

    owner = None

    def wheelEvent(self, ev, axis=None):
        o = self.owner
        if o is None:
            super().wheelEvent(ev, axis)
            return
        factor = 1.0 - (ev.delta() / 120.0) * 0.15
        if ev.modifiers() & Qt.ControlModifier:
            ymin, ymax = self.viewRange()[1]
            c, h = (ymin + ymax) / 2, (ymax - ymin) / 2 * factor
            self.setYRange(c - h, c + h, padding=0)
            o.y_manual = True
        elif o.follow:
            # En LIVE, la vue est pilotée par `x_span` : `refresh` la recalcule ancrée au
            # présent à chaque image. Changer la durée suffit.
            o.x_span = float(np.clip(o.x_span * factor, 10.0, 86400.0))
        else:
            # En LIBRE, `refresh` LIT viewRange() et n'applique JAMAIS `x_span` : sans ce
            # zoom explicite la molette est INERTE — et comme on `accept()` l'événement, le
            # zoom par défaut de pyqtgraph ne prend pas le relais non plus. Résultat : plus
            # moyen de dézoomer dès qu'un simple glissement avait quitté le live. Mesuré.
            # On part de la plage AFFICHÉE, pas de `x_span` : un pan ou un zoom-boîte peut
            # l'avoir désynchronisée, et zoomer depuis une durée fantôme ferait sauter la vue.
            x0, x1 = self.viewRange()[0]
            span = float(np.clip((x1 - x0) * factor, 10.0, 86400.0))
            c = (x0 + x1) / 2.0
            self.setXRange(c - span / 2.0, c + span / 2.0, padding=0)
            o.x_span = span      # « ● Live » conservera la durée que l'œil voit
        ev.accept()


# --- Footprint (port de Crypto gui/footprint_view.py) ----------------------

class FootprintItem(pg.GraphicsObject):
    def __init__(self) -> None:
        super().__init__()
        self.candles: list = []
        self.tick = 0.0
        self.picture = QtGui.QPicture()
        self._bounds = QRectF()
        self.show_bars = True
        self.show_numbers = True
        self.show_header = True
        self.show_poc = False

    def set_data(self, candles: list, tick: float) -> None:
        self.candles = candles
        self.tick = tick
        self._generate()
        self.prepareGeometryChange()
        self.update()

    def _generate(self) -> None:
        self.picture = QtGui.QPicture()
        if not self.candles or self.tick <= 0:
            self._bounds = QRectF()
            return
        tick = self.tick
        p = QtGui.QPainter(self.picture)
        for c in self.candles:
            w = c.t1 - c.t0
            x0 = c.t0
            xc = x0 + w * 0.5
            gap = w * CENTER_GAP
            maxlen = w * 0.5 - gap
            up = c.c >= c.o
            ccol = QColor(*BUY) if up else QColor(*SELL)

            # chandelier discret en arrière-plan
            body = QColor(ccol); body.setAlphaF(0.10)
            p.setPen(Qt.NoPen); p.setBrush(body)
            p.drawRect(QRectF(x0, min(c.o, c.c), w, max(abs(c.c - c.o), tick * 0.04)))
            obc = QColor(ccol); obc.setAlphaF(0.30)
            op = QPen(obc); op.setCosmetic(True); op.setWidth(1)
            p.setPen(op); p.setBrush(Qt.NoBrush)
            p.drawRect(QRectF(x0, min(c.o, c.c), w, max(abs(c.c - c.o), tick * 0.04)))

            rows = set(c.buy) | set(c.sell)
            cmax = 1e-9
            poc, poc_v = None, -1.0
            for r in rows:
                bv, sv = c.buy.get(r, 0.0), c.sell.get(r, 0.0)
                cmax = max(cmax, bv, sv)
                if bv + sv > poc_v:
                    poc_v, poc = bv + sv, r
            for r in (rows if self.show_bars else ()):
                bv, sv = c.buy.get(r, 0.0), c.sell.get(r, 0.0)
                if bv + sv <= 0:
                    continue
                yb = r * tick
                bh = tick * 0.74
                yo = yb + tick * 0.13
                if sv > 0:
                    ln = sv / cmax * maxlen * 0.98
                    col = QColor(*SELL)
                    col.setAlphaF(0.85 if sv >= IMB_RATIO * max(bv, 1e-9) else 0.5)
                    p.setPen(Qt.NoPen); p.setBrush(col)
                    p.drawRect(QRectF(xc - gap - ln, yo, ln, bh))
                if bv > 0:
                    ln = bv / cmax * maxlen * 0.98
                    col = QColor(*BUY)
                    col.setAlphaF(0.85 if bv >= IMB_RATIO * max(sv, 1e-9) else 0.5)
                    p.setPen(Qt.NoPen); p.setBrush(col)
                    p.drawRect(QRectF(xc + gap, yo, ln, bh))
                p.setBrush(Qt.NoBrush)
                p.setPen(POC_PEN if (self.show_poc and r == poc) else GRID_PEN)
                p.drawRect(QRectF(x0, yb, w, tick))

            # mèche (high-low) au centre, subtile
            wc = QColor(ccol); wc.setAlphaF(0.55)
            wp = QPen(wc); wp.setCosmetic(True); wp.setWidth(1)
            p.setPen(wp)
            p.drawLine(QPointF(xc, c.l), QPointF(xc, c.h))
        p.end()

        tmin = self.candles[0].t0
        tmax = self.candles[-1].t1
        pmin = min(c.l for c in self.candles)
        pmax = max(c.h for c in self.candles)
        self._bounds = QRectF(tmin, pmin, tmax - tmin, max(pmax - pmin, tick))

    def boundingRect(self):
        return self._bounds

    def paint(self, p, *args):
        p.drawPicture(0, 0, self.picture)
        vb = self.getViewBox()
        if vb is None or not self.candles or self.tick <= 0:
            return
        w0 = self.candles[0].t1 - self.candles[0].t0
        o = vb.mapViewToDevice(QPointF(0.0, 0.0))
        ex = vb.mapViewToDevice(QPointF(w0, 0.0))
        ey = vb.mapViewToDevice(QPointF(0.0, self.tick))
        if o is None or ex is None or ey is None:
            return
        col_px = abs(ex.x() - o.x())
        row_px = abs(ey.y() - o.y())
        (xr0, xr1), (yr0, yr1) = vb.viewRange()
        top_px = vb.mapViewToDevice(QPointF(0.0, yr1)).y()

        p.save()
        p.resetTransform()
        if self.show_header and col_px >= 42:
            hf = QFont(); hf.setPixelSize(11); hf.setBold(True)
            p.setFont(hf)
            for c in self.candles:
                if c.t1 < xr0 or c.t0 > xr1:
                    continue
                xc = c.t0 + (c.t1 - c.t0) * 0.5
                rows = c.buy.keys() | c.sell.keys()
                top_price = c.h
                if rows:
                    top_price = max(top_price, (max(rows) + 1) * self.tick)
                dh = vb.mapViewToDevice(QPointF(xc, top_price))
                if dh is None:
                    continue
                ytop = max(dh.y() - 32, top_px + 1)
                p.setPen(QPen(QColor(170, 182, 196)))
                p.drawText(QRectF(dh.x() - col_px / 2, ytop, col_px, 13),
                           Qt.AlignCenter, f"V {_fmt(c.buy_total + c.sell_total)}")
                p.setPen(QPen(QColor(*BUY) if c.delta >= 0 else QColor(*SELL)))
                p.drawText(QRectF(dh.x() - col_px / 2, ytop + 13, col_px, 13),
                           Qt.AlignCenter, f"D {c.delta:+.2f}")
        if self.show_numbers and col_px >= MIN_COL_PX and row_px >= MIN_ROW_PX:
            f = QFont(); f.setPixelSize(int(min(row_px * 0.6, 12)))
            p.setFont(f)
            gap_px = col_px * CENTER_GAP
            cw = min(col_px * 0.5 - gap_px, 56.0)
            sell_pen = QPen(QColor(255, 175, 175))
            buy_pen = QPen(QColor(165, 248, 210))
            for c in self.candles:
                if c.t1 < xr0 or c.t0 > xr1:
                    continue
                xc = c.t0 + (c.t1 - c.t0) * 0.5
                for r in set(c.buy) | set(c.sell):
                    yc = (r + 0.5) * self.tick
                    if yc < yr0 or yc > yr1:
                        continue
                    bv, sv = c.buy.get(r, 0.0), c.sell.get(r, 0.0)
                    if bv + sv <= 0:
                        continue
                    d = vb.mapViewToDevice(QPointF(xc, yc))
                    if d is None:
                        continue
                    p.setPen(sell_pen)
                    p.drawText(QRectF(d.x() - gap_px - cw, d.y() - row_px / 2, cw, row_px),
                               Qt.AlignRight | Qt.AlignVCenter, _fmt(sv))
                    p.setPen(buy_pen)
                    p.drawText(QRectF(d.x() + gap_px, d.y() - row_px / 2, cw, row_px),
                               Qt.AlignLeft | Qt.AlignVCenter, _fmt(bv))
        p.restore()


# --- Panneau combiné -------------------------------------------------------

class FlowPanel(QtWidgets.QWidget):
    """Vue complète d'un symbole : graphe orderflow/footprint + échelle DOM.

    Ne porte AUCUN réglage : la barre de commandes vit dans `ui.py` (comme chez crypto,
    où `gui/app.py` tient la barre et `gui/orderflow_view.py` ne fait que rendre). Ce
    panneau n'expose que l'API que cette barre et `controls.py` pilotent.
    """

    def __init__(self, symbol: str, store) -> None:
        super().__init__()
        self.symbol = symbol
        self.store = store
        self.follow = True
        self.x_span = float(config.LIVE_SPAN_SECONDS)
        self.res_s = float(config.RESOLUTION_SECONDS)
        self.y_manual = False
        self._yr = None
        self._vis_price = None
        self._inst_tick = 0.0       # tick d'instrument STABLE (plus petit écart vu)
        self._foot_tick = 0.0       # tick de regroupement footprint (hystérésis)
        # réglages du popup « Trades » (voir controls.py)
        self.min_size = 0.0         # n'afficher que les trades de taille >= seuil
        self.auto_filter = True     # limite la densité de points selon la plage affichée
        self.dot_scale = 1.0        # multiplicateur de taille des points (1.0 = nominal)
        self._auto_thresh = 0.0     # seuil de taille STABLE du filtre Auto (hystérésis)

        root = QtWidgets.QVBoxLayout(self)
        root.setContentsMargins(4, 4, 4, 4)

        row = QtWidgets.QHBoxLayout()
        root.addLayout(row, stretch=1)

        self.vb = FlowViewBox()
        self.vb.owner = self
        # Référence gardée : `set_resolution` lui cale son pas minimal (voir TzDateAxis).
        self._taxis = TzDateAxis(orientation="bottom")
        self.plot = pg.PlotWidget(
            viewBox=self.vb,
            axisItems={"bottom": self._taxis,
                       "right": PriceAxis(orientation="right")},
        )
        self.plot.showAxis("right")
        self.plot.hideAxis("left")
        self.plot.setTitle(symbol, color="w", size="12pt")
        self.plot.showGrid(x=True, y=True, alpha=0.15)
        # Tout glissement/pan à la souris fait QUITTER le live (le bouton « ● Live » de la
        # barre reflète ensuite cet état). Sans ce branchement, `follow` ne repassait jamais
        # à False : la vue restait collée au présent et « Aller au » n'aurait rien pu faire.
        self.vb.sigRangeChangedManually.connect(self._on_manual_range)

        self.img = pg.ImageItem()
        self.img.setLookupTable(_HEAT.getLookupTable(0.0, 1.0, 256))
        self.img.setOpacity(config.HEAT_OPACITY)
        self.plot.addItem(self.img)

        self.bid_line = pg.PlotCurveItem(pen=pg.mkPen(BUY + (180,), width=1))
        self.ask_line = pg.PlotCurveItem(pen=pg.mkPen(SELL + (180,), width=1))
        self.plot.addItem(self.bid_line); self.plot.addItem(self.ask_line)

        dot_pen = pg.mkPen(20, 20, 20, 90)
        self.buys = pg.ScatterPlotItem(pen=dot_pen, brush=pg.mkBrush(*BUY, 140), pxMode=True)
        self.sells = pg.ScatterPlotItem(pen=dot_pen, brush=pg.mkBrush(*SELL, 140), pxMode=True)
        self.plot.addItem(self.sells); self.plot.addItem(self.buys)

        self.fp = FootprintItem()
        self.plot.addItem(self.fp)

        # COUCHES superposées, pilotées par « Affichage ⚙ » (controls.py). Le carnet (DOM)
        # est un panneau latéral -> traité à part. Les lignes bid/ask sont le contexte de la
        # heatmap : elles vivent et s'effacent avec elle, comme chez le frère.
        self._layers = {
            "footprint": (self.fp,),
            "heatmap": (self.img, self.bid_line, self.ask_line),
            "trades": (self.buys, self.sells),
        }
        self._layer_order = ["heatmap", "trades", "footprint"]   # arrière -> avant
        self._apply_z_order()
        row.addWidget(self.plot, stretch=1)

        # BANDEAU d'explication, posé SUR le graphe (voir `set_notice`). Un QLabel enfant
        # plutôt qu'un TextItem : il ne doit ni bouger ni grossir avec le zoom, et surtout
        # ne pas vivre dans le repère des données — ce qu'il dit, c'est justement que les
        # données manquent. Placé en HAUT, pas au centre : le footprint doit rester lisible
        # dessous (sur IBKR, les trades s'affichent bel et bien — seul le carnet manque).
        self._notice = QtWidgets.QLabel("", self.plot)
        self._notice.setAlignment(Qt.AlignCenter)
        # Sans repli, `adjustSize` étale la phrase sur TOUTE la largeur du graphe : le
        # bandeau devient une barre qui écrase la vue au lieu de l'annoter.
        self._notice.setWordWrap(True)
        self._notice.setStyleSheet(
            "QLabel { color: #e6c07b; background: rgba(20,22,28,215);"
            " border: 1px solid #4b4033; border-radius: 4px; padding: 5px 10px;"
            " font-size: 11px; }")
        self._notice.hide()

        # échelle DOM (carnet courant), Y-liée au graphe principal
        self.dom = pg.PlotWidget()
        self.dom.setMaximumWidth(150)
        # ⚠️ TITRE VIDE OBLIGATOIRE — NE PAS « NETTOYER ».
        # `setYLink` synchronise la PLAGE de prix, pas la GÉOMÉTRIE. Le graphe principal porte
        # un titre (`setTitle(symbol)`) qui lui mange 30 px en haut ; sans titre, la zone de
        # tracé du DOM commençait 30 px plus haut. Pire : pyqtgraph tente alors de compenser
        # en décalant la plage du DOM, et se trompe de sens — **désalignement MESURÉ de 60 px
        # (= 2 × 30), constant sur toute la hauteur**, avec des plages Y différentes (21,30
        # contre 22,13 pts). Un titre vide de MÊME TAILLE rend les deux géométries identiques :
        # la compensation devient un no-op et le carnet retombe en face des bons niveaux.
        # Si le titre du graphe change de taille, celui-ci DOIT suivre.
        self.dom.setTitle(" ", size="12pt")
        self.dom.setYLink(self.plot)
        self.dom.hideAxis("left")
        self.dom.showGrid(x=True, y=False, alpha=0.1)
        self.dom_bids = pg.BarGraphItem(x0=0, y=[], height=0, width=[], brush=pg.mkBrush(*BUY, 150))
        self.dom_asks = pg.BarGraphItem(x0=0, y=[], height=0, width=[], brush=pg.mkBrush(*SELL, 150))
        self.dom.addItem(self.dom_bids); self.dom.addItem(self.dom_asks)
        # Le DOM est vide en entier quand le carnet manque : il porte donc sa propre étiquette
        # (le panneau ne fait que 150 px — texte court obligatoire).
        self._dom_notice = QtWidgets.QLabel("", self.dom)
        self._dom_notice.setAlignment(Qt.AlignCenter)
        self._dom_notice.setWordWrap(True)
        self._dom_notice.setStyleSheet(
            "QLabel { color: #8b949e; background: transparent; font-size: 10px; }")
        self._dom_notice.hide()
        row.addWidget(self.dom)

    def set_notice(self, text: str | None, dom_text: str | None = None) -> None:
        """Affiche (ou retire) le bandeau d'explication posé sur les graphes.

        Décidé par `MainWindow`, qui est le seul à savoir POURQUOI une couche est vide :
        la vue, elle, ne voit qu'un store. Un graphe muet sans explication est
        indiscernable d'une panne — c'est précisément ce qui a fait perdre du temps.
        """
        for label, txt in ((self._notice, text), (self._dom_notice, dom_text)):
            if txt:
                label.setText(txt)
                label.setVisible(True)
            else:
                label.setVisible(False)
        self._place_notices()

    def _place_notices(self) -> None:
        if self._notice.isVisible():
            r = self.plot.rect()
            # Borné à la moitié de la largeur (et 560 px) : un bandeau qui traverse l'écran
            # cesse d'être une annotation. `setFixedWidth` AVANT `adjustSize` pour que le
            # repli calcule la hauteur sur la bonne largeur.
            self._notice.setFixedWidth(max(220, min(560, r.width() // 2)))
            self._notice.adjustSize()
            self._notice.move(max(0, r.center().x() - self._notice.width() // 2), 34)
        if self._dom_notice.isVisible():
            r = self.dom.rect()
            self._dom_notice.setFixedWidth(max(40, r.width() - 12))
            self._dom_notice.adjustSize()
            self._dom_notice.move(6, max(0, r.center().y() - self._dom_notice.height() // 2))

    def resizeEvent(self, ev) -> None:
        super().resizeEvent(ev)
        self._place_notices()

    # -- modes : live (collé au présent) / libre (navigation dans l'historique) --
    def _on_manual_range(self) -> None:
        if self.follow:
            self.follow = False

    def go_live(self) -> None:
        """Recolle au présent en CONSERVANT la durée affichée (15 min reste 15 min)."""
        (x0, x1), _ = self.vb.viewRange()
        span = x1 - x0
        if span > 0:
            self.x_span = float(np.clip(span, 10.0, 86400.0))
        self.follow = True
        self.y_manual = False
        self._yr = None

    def goto(self, t_start: float) -> None:
        """Saute à une date précise (epoch s), en mode libre.

        N'a de sens que depuis la phase 4 : au-delà de l'heure gardée en mémoire, c'est
        `BookReader` qui sert la heatmap depuis books.db. Le footprint, lui, reste borné à
        la mémoire (`FlowStore.visible_trades` ne lit pas le disque) : sur une date
        ancienne, la heatmap répond et les bougies non. C'est attendu, pas un bug.
        """
        self.follow = False
        self.y_manual = False
        self._yr = None
        self.vb.setXRange(t_start, t_start + self.x_span, padding=0)

    def set_resolution(self, res_s: float) -> None:
        self.res_s = max(1.0, float(res_s))
        # La grille suit la résolution : pas de graduation plus fine qu'une bougie.
        self._taxis.min_step = self.res_s
        self._taxis.picture = None      # force le recalcul des graduations à la prochaine image
        self._taxis.update()

    def set_symbol(self, symbol: str, store) -> None:
        """Bascule le panneau sur un autre symbole / accès (boutons de la barre, menu Accès).

        Remet à zéro tout ce qui est DÉDUIT du flux précédent : garder le tick de l'ES pour
        dessiner le NQ donnerait une grille de prix fausse, et garder l'échelle Y ferait
        sauter la vue. Le lissage `_yr` repart de la première mesure du nouveau flux.
        """
        self.symbol = symbol
        self.store = store
        self.plot.setTitle(symbol, color="w", size="12pt")
        self._inst_tick = 0.0
        self._foot_tick = 0.0
        self._yr = None
        self._vis_price = None
        self._auto_thresh = 0.0

    # -- API des couches (pilotée par controls.py / « Affichage ⚙ ») --------
    def _apply_z_order(self) -> None:
        """Assigne les Z selon l'ordre courant (bas -> haut = arrière -> avant)."""
        for i, name in enumerate(self._layer_order):
            for it in self._layers[name]:
                it.setZValue((i + 1) * 10)

    def set_layer_visible(self, name: str, on: bool) -> None:
        for it in self._layers.get(name, ()):
            it.setVisible(on)

    def set_layer_opacity(self, name: str, value: float) -> None:
        v = float(np.clip(value, 0.0, 1.0))
        for it in self._layers.get(name, ()):
            it.setOpacity(v)

    def move_layer(self, name: str, to_front: bool) -> None:
        order = self._layer_order
        if name not in order:
            return
        i = order.index(name)
        j = i + 1 if to_front else i - 1
        if 0 <= j < len(order):
            order[i], order[j] = order[j], order[i]
            self._apply_z_order()

    def set_dom_visible(self, on: bool) -> None:
        self.dom.setVisible(on)

    def set_footprint_option(self, name: str, on: bool) -> None:
        """Donnée affichée du footprint (show_bars / show_numbers / show_header / show_poc).

        `show_bars` et `show_poc` sont lus dans `_generate` (la QPicture) tandis que
        `show_numbers` et `show_header` le sont dans `paint` : on REGÉNÈRE donc, sinon les
        deux premiers ne changeraient rien à l'écran jusqu'au prochain trade.
        """
        if not hasattr(self.fp, name):
            return
        setattr(self.fp, name, bool(on))
        self.fp.set_data(self.fp.candles, self.fp.tick)

    def set_dot_scale(self, value: float) -> None:
        self.dot_scale = max(0.1, float(value))

    def set_min_size(self, value: float) -> None:
        self.min_size = max(0.0, float(value))

    # -- helpers grille (commune heatmap/DOM) ------------------------------
    @staticmethod
    def _book_levels(book):
        """(bids, asks) en arrays numpy de prix/tailles à partir d'un snapshot."""
        _, b, a = book
        bp = np.array([p for p, _ in b]); bs = np.array([s for _, s in b])
        ap = np.array([p for p, _ in a]); as_ = np.array([s for _, s in a])
        return bp, bs, ap, as_

    def _inst_tick_from(self, ap: np.ndarray) -> float:
        """Tick d'instrument = plus petit écart entre niveaux ask (mémorisé monotone)."""
        if ap.size >= 2:
            gaps = np.diff(np.sort(ap))
            gaps = gaps[gaps > 0]
            if gaps.size:
                g = float(gaps.min())
                self._inst_tick = g if self._inst_tick <= 0 else min(self._inst_tick, g)
        return self._inst_tick if self._inst_tick > 0 else 0.25

    @staticmethod
    def _nice(x: float) -> float:
        if x <= 1.0:
            return 1.0
        k = math.floor(math.log10(x))
        base = 10.0 ** k
        for m in (1.0, 2.0, 5.0, 10.0):
            if m * base >= x:
                return m * base
        return 10.0 * base

    def _footprint_tick(self, yspan: float, inst: float) -> float:
        """Tick de regroupement du footprint, avec hystérésis (lisibilité 12..MAX_ROWS)."""
        if yspan <= 0:
            return self._foot_tick or inst
        cur = self._foot_tick
        if cur > 0 and 12.0 <= yspan / cur <= MAX_ROWS:
            return cur
        target = yspan / (MAX_ROWS * 0.7)
        mult = self._nice(target / inst) if target > inst else 1.0
        self._foot_tick = inst * mult
        return self._foot_tick

    def _grid(self, lo: float, hi: float, tick: float):
        """Grille de prix COMMUNE heatmap/DOM. Renvoie (tick, row0, nb, y0, hauteur)."""
        if tick <= 0:
            tick = (hi - lo) / 200.0 if hi > lo else 1.0
        row_lo = int(np.floor(lo / tick))
        row_hi = int(np.ceil(hi / tick))
        nb = row_hi - row_lo
        if nb > NBINS_MAX:
            k = int(np.ceil(nb / NBINS_MAX))
            tick *= k
            row_lo = int(np.floor(lo / tick))
            row_hi = int(np.ceil(hi / tick))
            nb = row_hi - row_lo
        nb = max(nb, 1)
        return tick, row_lo, nb, row_lo * tick, nb * tick

    @staticmethod
    def _bin(prices, sizes, tick, row0, nb):
        out = np.zeros(nb, dtype=np.float32)
        if prices.size == 0:
            return out
        rows = np.round(prices / tick).astype(np.int64) - row0
        m = (rows >= 0) & (rows < nb)
        if m.any():
            np.add.at(out, rows[m], sizes[m])
        return out

    def _auto_y(self, book):
        """Cadrage vertical. Le carnet est l'ancre PRÉFÉRÉE, jamais la seule.

        ⚠️ Cette fonction exigeait un carnet et renonçait sans lui — or **un accès sans L2
        (IBKR non abonné) n'en a JAMAIS**. L'axe restait donc figé sur le dernier cadrage
        connu (celui de Quantower), et les trades d'IBKR se dessinaient hors écran : vue
        vide, alors que les points existaient. Mesuré : axe à 29642-29660 pendant que les
        trades tombaient à 29284-29291, **355 points plus bas**.
        🪤 Un test qui compte `scatter.getData()` voit 23 points et conclut « ça marche » :
        il mesure les données POSÉES sur l'objet, pas ce qui est VISIBLE. C'est comme ça
        que la panne est passée. Vérifier le cadrage, pas seulement le contenu.
        """
        lo = hi = None
        if book is not None:
            bp, _, ap, _ = self._book_levels(book)
            if bp.size and ap.size:
                # Le pont sert ~100 niveaux PAR CÔTÉ pour donner de la profondeur à la
                # heatmap ; l'AXE, lui, n'a aucune raison de tous les montrer. Les englober
                # étirait la vue à ~50 points sur le NQ, et c'est CETTE étendue qui forçait
                # le footprint à regrouper 10 ticks par ligne alors que le DOM en montre 1
                # (mesuré). On ne garde qu'une bande de `config.VIEW_LEVELS` niveaux autour
                # du marché. Niveaux servis MEILLEUR EN PREMIER (vérifié sur 49 snapshots).
                n = max(1, int(config.VIEW_LEVELS))
                lo, hi = float(bp[:n].min()), float(ap[:n].max())
        # Les TRADES visibles élargissent le cadrage — et le portent à eux seuls quand il
        # n'y a pas de carnet. Sans ça, en zoom arrière (ou sans L2), le prix sort de l'écran
        # et l'historique paraît vide.
        if self._vis_price is not None:
            lo = self._vis_price[0] if lo is None else min(lo, self._vis_price[0])
            hi = self._vis_price[1] if hi is None else max(hi, self._vis_price[1])
        if lo is None or hi is None:
            return None
        if hi <= lo:
            # Tous les trades au même prix : sans marge, la plage serait dégénérée.
            span = (self._inst_tick or 0.25) * 10
            lo, hi = lo - span, hi + span
        pad = (hi - lo) * Y_PAD
        target = (lo - pad, hi + pad)
        if self._yr is None:
            self._yr = target
        else:
            self._yr = (self._yr[0] + (target[0] - self._yr[0]) * Y_EMA,
                        self._yr[1] + (target[1] - self._yr[1]) * Y_EMA)
        return self._yr

    def _clear(self) -> None:
        """Vide TOUTES les couches.

        Appelé dès que le store courant n'a rien à montrer. Sans ça, `refresh` sortait en
        avance et laissait à l'écran les pixels du flux PRÉCÉDENT : basculer de Démo vers un
        Quantower muet affichait les chiffres de la démo sous l'étiquette « Quantower ».
        Mesuré : 1226 points fantômes, 25 bougies. Un tableau de bord dont l'argument est
        « d'où vient la donnée » ne peut pas se permettre ça.
        """
        self.img.clear()
        self.bid_line.clear()
        self.ask_line.clear()
        self.buys.setData(x=[], y=[], size=[])
        self.sells.setData(x=[], y=[], size=[])
        self.fp.set_data([], 0.0)
        self.dom_bids.setOpts(x0=0, y=[], height=0, width=[])
        self.dom_asks.setOpts(x0=0, y=[], height=0, width=[])
        self._vis_price = None

    # -- rendu (appelé par le QTimer de la fenêtre) ------------------------
    def refresh(self) -> None:
        latest = self.store.t_last
        if not latest:
            self._clear()
            return
        book = self.store.last_book()

        if self.follow:
            margin = self.x_span * RIGHT_MARGIN_FRAC
            candle_end = math.floor(latest / self.res_s) * self.res_s + self.res_s
            t1 = max(latest, candle_end) + margin
            t0 = t1 - self.x_span
            self.vb.setXRange(t0, t1, padding=0)
            # PAS de `and book is not None` : `_auto_y` sait se rabattre sur les trades, et
            # c'est le seul cadrage possible pour un accès sans carnet (voir sa docstring).
            if not self.y_manual:
                yr = self._auto_y(book)
                if yr:
                    self.vb.setYRange(*yr, padding=0)
            yr = tuple(self.vb.viewRange()[1])
        else:
            t0, t1 = self.vb.viewRange()[0]
            yr = tuple(self.vb.viewRange()[1])

        lo, hi = yr
        inst = 0.25
        tick = inst
        if book is not None:
            _, _, ap, _ = self._book_levels(book)
            inst = self._inst_tick_from(ap)
            tick = inst
        grid = self._grid(lo, hi, tick) if hi > lo else None

        self._draw_heatmap(t0, t1, grid)
        self._draw_scatter(t0, t1)
        self._draw_bbo(t0, t1)
        if self.fp.isVisible():
            self._draw_footprint(t0, t1, yr[1] - yr[0], inst)
        self._draw_dom(book, grid)

    def _draw_heatmap(self, t0, t1, grid) -> None:
        cols = self.store.visible_books(t0, t1)
        if not cols or grid is None:
            self.img.clear()
            return
        tick, row0, nb, y0, height = grid
        span = max(t1 - t0, 1e-3)
        nt = int(min(MAX_RENDER_COLS, max(1.0, span / SAMPLE_HINT_S)))
        dt = span / nt
        arr = np.zeros((nt, nb), dtype=np.float32)
        for ts, b, a in cols:
            j = int((ts - t0) / dt)
            if not (0 <= j < nt):
                continue
            prices = np.array([p for p, _ in b] + [p for p, _ in a])
            sizes = np.array([s for _, s in b] + [s for _, s in a])
            arr[j] = self._bin(prices, sizes, tick, row0, nb)
        np.log1p(arr, out=arr)
        pos = arr[arr > 0]
        vmax = float(np.quantile(pos, 0.95)) if pos.size else 1.0
        self.img.setImage(arr, autoLevels=False, levels=(0.0, max(vmax, 1e-6)))
        self.img.setRect(QRectF(t0, y0, span, height))

    def _draw_scatter(self, t0, t1) -> None:
        trades = self.store.visible_trades(t0, t1)
        n = len(trades)
        if n == 0:
            self.buys.setData(x=[], y=[], size=[])
            self.sells.setData(x=[], y=[], size=[])
            self._vis_price = None
            return
        ts = np.fromiter((t.ts for t in trades), np.float64, n) / 1000.0
        price = np.fromiter((t.price for t in trades), np.float64, n)
        size = np.fromiter((t.size for t in trades), np.float64, n)
        is_buy = np.fromiter((t.side == "buy" for t in trades), bool, n)
        # L'étendue Y couvre TOUS les trades de la fenêtre, filtrés ou non : ce que l'on
        # cache pour désencombrer le rendu ne doit pas sortir la vue de son cadrage.
        self._vis_price = (float(price.min()), float(price.max()))

        idx = self._filter_trades(size, t0, t1)
        # ORDRE DE DESSIN STABLE : tri par volume CROISSANT -> les gros points passent en
        # dernier (donc au-dessus), et l'empilement ne permute plus d'une image à l'autre.
        idx = idx[np.argsort(size[idx], kind="stable")]
        dot = np.clip(2.0 + np.sqrt(size[idx]) * DOT_SCALE, DOT_MIN_PX, DOT_MAX_PX)
        dot *= self.dot_scale
        np.minimum(dot, DOT_MAX_PX * 1.6, out=dot)   # même garde-fou que le frère
        b = is_buy[idx]
        self.buys.setData(x=ts[idx][b], y=price[idx][b], size=dot[b])
        self.sells.setData(x=ts[idx][~b], y=price[idx][~b], size=dot[~b])

    def _filter_trades(self, size: np.ndarray, t0: float, t1: float) -> np.ndarray:
        """Indices des trades à DESSINER (le store en garde bien plus qu'on n'en affiche).

        Deux filtres, tous deux vectorisés : le seuil manuel `min_size`, puis le filtre
        `auto_filter` dont la cible DÉCROÎT quand la plage s'élargit (dézoomer ne doit pas
        noyer l'écran de points). Le seuil auto est STABLE par hystérésis : un top-N
        recalculé à chaque image ferait clignoter les points en bord de sélection.
        """
        idx = np.nonzero(size >= self.min_size)[0]
        if self.auto_filter:
            span = max(t1 - t0, 1.0)
            cap = int(np.clip(MAX_POINTS * (config.LIVE_SPAN_SECONDS / span) ** AUTO_DECAY,
                              MIN_POINTS, MAX_POINTS))
            if idx.size > cap:
                target = float(np.partition(size[idx], -cap)[-cap])
                prev = self._auto_thresh
                if prev <= 0 or not (0.8 <= target / max(prev, 1e-9) <= 1.25):
                    self._auto_thresh = target       # dérive nette -> on réajuste
                idx = idx[size[idx] >= max(self.min_size, self._auto_thresh)]
        if idx.size > HARD_CAP:                      # garde-fou de rendu
            idx = idx[np.argpartition(size[idx], -HARD_CAP)[-HARD_CAP:]]
        return idx

    @staticmethod
    def _ffill(a: np.ndarray) -> np.ndarray:
        """Remplit les NaN par la dernière valeur connue (puis backfill au début)."""
        idx = np.where(~np.isnan(a), np.arange(a.size), 0)
        np.maximum.accumulate(idx, out=idx)
        out = a[idx]
        if np.isnan(out).any():                 # NaN initiaux -> 1ère valeur valide
            valid = a[~np.isnan(a)]
            if valid.size:
                out = np.where(np.isnan(out), valid[0], out)
        return out

    def _draw_bbo(self, t0, t1) -> None:
        cols = self.store.visible_books(t0, t1)
        if len(cols) < 2:
            self.bid_line.clear(); self.ask_line.clear()
            return
        ts = np.array([c[0] for c in cols])
        bid = np.array([c[1][0][0] if c[1] else np.nan for c in cols])
        ask = np.array([c[2][0][0] if c[2] else np.nan for c in cols])
        if np.isnan(bid).all() or np.isnan(ask).all():
            self.bid_line.clear(); self.ask_line.clear()
            return
        self.bid_line.setData(ts, self._ffill(bid))
        self.ask_line.setData(ts, self._ffill(ask))

    def _draw_footprint(self, t0, t1, yspan, inst) -> None:
        tick = self._footprint_tick(yspan, inst)
        win = self.store.visible_trades(t0, t1)
        candles = build_candles(win, self.res_s, tick)
        self.fp.set_data(candles, tick)

    def _draw_dom(self, book, grid) -> None:
        if grid is None or book is None:
            self.dom_bids.setOpts(x0=0, y=[], height=0, width=[])
            self.dom_asks.setOpts(x0=0, y=[], height=0, width=[])
            return
        tick, row0, nb, y0, _ = grid
        bp, bs, ap, as_ = self._book_levels(book)
        if bp.size == 0 and ap.size == 0:
            return
        centers = y0 + (np.arange(nb) + 0.5) * tick
        bid_v = self._bin(bp, bs, tick, row0, nb)
        ask_v = self._bin(ap, as_, tick, row0, nb)
        bm, am = bid_v > 0, ask_v > 0
        self.dom_bids.setOpts(x0=0, y=centers[bm], height=tick * 0.9, width=bid_v[bm])
        self.dom_asks.setOpts(x0=0, y=centers[am], height=tick * 0.9, width=ask_v[am])
        mx = float(max(bid_v.max(), ask_v.max(), 1e-9))
        self.dom.setXRange(0, mx * 1.1, padding=0)
