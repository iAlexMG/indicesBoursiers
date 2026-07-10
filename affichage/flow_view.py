"""Vue combinée orderflow + orderbook + footprint (style projet Crypto), en PyQt5.

Un seul graphe par symbole superpose, du fond vers l'avant :
  - HEATMAP de liquidité (snapshots de carnet binnés dans le temps, type Bookmap),
  - lignes BEST BID / BEST ASK,
  - SCATTER des trades (taille ∝ volume, vert acheteur / rouge vendeur),
  - FOOTPRINT (chandelier discret + histogramme bid/ask par niveau, type Quantower).
À droite, une échelle DOM verticale (carnet courant) alignée sur la même grille de prix.

Porté/adapté de Crypto `gui/orderflow_view.py` et `gui/footprint_view.py`, sans aucune
dépendance au backend Crypto : la vue lit un `orderflow_data.FlowStore`.
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

MAX_ROWS = 46            # nb max de lignes de prix du footprint -> tick adaptatif
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
DOT_SCALE = 3.0          # facteur de taille des points (scatter)

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
    """Axe temps au fuseau configuré ; graduations calculées depuis la plage."""

    def tickValues(self, minVal, maxVal, size):
        span = maxVal - minVal
        if span <= 0:
            return []
        spacing = _TIME_STEPS[-1]
        for s in _TIME_STEPS:
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
            ev.accept()
        else:
            o.x_span = float(np.clip(o.x_span * factor, 10.0, 86400.0))
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
    """Vue complète d'un symbole : graphe orderflow/footprint + échelle DOM + options."""

    def __init__(self, symbol: str, store) -> None:
        super().__init__()
        self.symbol = symbol
        self.store = store
        self.follow = True
        self.x_span = float(config.LIVE_SPAN_SECONDS)
        self.y_manual = False
        self._yr = None
        self._vis_price = None
        self._inst_tick = 0.0       # tick d'instrument STABLE (plus petit écart vu)
        self._foot_tick = 0.0       # tick de regroupement footprint (hystérésis)

        root = QtWidgets.QVBoxLayout(self)
        root.setContentsMargins(4, 4, 4, 4)
        root.addLayout(self._build_options())

        row = QtWidgets.QHBoxLayout()
        root.addLayout(row, stretch=1)

        self.vb = FlowViewBox()
        self.vb.owner = self
        self.plot = pg.PlotWidget(
            viewBox=self.vb,
            axisItems={"bottom": TzDateAxis(orientation="bottom"),
                       "right": PriceAxis(orientation="right")},
        )
        self.plot.showAxis("right")
        self.plot.hideAxis("left")
        self.plot.setTitle(symbol, color="w", size="12pt")
        self.plot.showGrid(x=True, y=True, alpha=0.15)

        self.img = pg.ImageItem()
        self.img.setLookupTable(_HEAT.getLookupTable(0.0, 1.0, 256))
        self.img.setOpacity(config.HEAT_OPACITY)
        self.img.setZValue(0)
        self.plot.addItem(self.img)

        self.bid_line = pg.PlotCurveItem(pen=pg.mkPen(BUY + (180,), width=1))
        self.ask_line = pg.PlotCurveItem(pen=pg.mkPen(SELL + (180,), width=1))
        self.bid_line.setZValue(10); self.ask_line.setZValue(10)
        self.plot.addItem(self.bid_line); self.plot.addItem(self.ask_line)

        dot_pen = pg.mkPen(20, 20, 20, 90)
        self.buys = pg.ScatterPlotItem(pen=dot_pen, brush=pg.mkBrush(*BUY, 140), pxMode=True)
        self.sells = pg.ScatterPlotItem(pen=dot_pen, brush=pg.mkBrush(*SELL, 140), pxMode=True)
        self.buys.setZValue(20); self.sells.setZValue(20)
        self.plot.addItem(self.sells); self.plot.addItem(self.buys)

        self.fp = FootprintItem()
        self.fp.setZValue(30)
        self.plot.addItem(self.fp)
        row.addWidget(self.plot, stretch=1)

        # échelle DOM (carnet courant), Y-liée au graphe principal
        self.dom = pg.PlotWidget()
        self.dom.setMaximumWidth(150)
        self.dom.setYLink(self.plot)
        self.dom.hideAxis("left")
        self.dom.showGrid(x=True, y=False, alpha=0.1)
        self.dom_bids = pg.BarGraphItem(x0=0, y=[], height=0, width=[], brush=pg.mkBrush(*BUY, 150))
        self.dom_asks = pg.BarGraphItem(x0=0, y=[], height=0, width=[], brush=pg.mkBrush(*SELL, 150))
        self.dom.addItem(self.dom_bids); self.dom.addItem(self.dom_asks)
        row.addWidget(self.dom)

    # -- barre d'options ---------------------------------------------------
    def _build_options(self):
        bar = QtWidgets.QHBoxLayout()
        self.cb_foot = QtWidgets.QCheckBox("Footprint"); self.cb_foot.setChecked(True)
        self.cb_heat = QtWidgets.QCheckBox("Heatmap"); self.cb_heat.setChecked(True)
        self.cb_trades = QtWidgets.QCheckBox("Trades"); self.cb_trades.setChecked(True)
        self.cb_dom = QtWidgets.QCheckBox("Carnet"); self.cb_dom.setChecked(True)
        for cb in (self.cb_foot, self.cb_heat, self.cb_trades, self.cb_dom):
            cb.stateChanged.connect(self._apply_layers)
            bar.addWidget(cb)
        bar.addSpacing(16)
        bar.addWidget(QtWidgets.QLabel("Résolution :"))
        self.res_combo = QtWidgets.QComboBox()
        self._res_values = [1, 5, 15, 30, 60, 300]
        for s in self._res_values:
            self.res_combo.addItem(f"{s}s" if s < 60 else f"{s // 60}m", s)
        self.res_combo.setCurrentIndex(self._res_values.index(config.RESOLUTION_SECONDS)
                                       if config.RESOLUTION_SECONDS in self._res_values else 1)
        self.res_s = float(config.RESOLUTION_SECONDS)
        self.res_combo.currentIndexChanged.connect(self._on_res)
        bar.addWidget(self.res_combo)
        btn_live = QtWidgets.QPushButton("⟳ Live")
        btn_live.clicked.connect(self._go_live)
        bar.addWidget(btn_live)
        bar.addStretch(1)
        return bar

    def _apply_layers(self) -> None:
        self.fp.setVisible(self.cb_foot.isChecked())
        for it in (self.img, self.bid_line, self.ask_line):
            it.setVisible(self.cb_heat.isChecked())
        self.buys.setVisible(self.cb_trades.isChecked())
        self.sells.setVisible(self.cb_trades.isChecked())
        self.dom.setVisible(self.cb_dom.isChecked())

    def _on_res(self, idx: int) -> None:
        self.res_s = float(self.res_combo.currentData())

    def _go_live(self) -> None:
        self.follow = True
        self.y_manual = False
        self._yr = None

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
        bp, _, ap, _ = self._book_levels(book)
        if bp.size == 0 or ap.size == 0:
            return None
        lo, hi = float(bp.min()), float(ap.max())
        if hi <= lo:
            return None
        if self._vis_price is not None:
            lo = min(lo, self._vis_price[0])
            hi = max(hi, self._vis_price[1])
        pad = (hi - lo) * Y_PAD
        target = (lo - pad, hi + pad)
        if self._yr is None:
            self._yr = target
        else:
            self._yr = (self._yr[0] + (target[0] - self._yr[0]) * Y_EMA,
                        self._yr[1] + (target[1] - self._yr[1]) * Y_EMA)
        return self._yr

    # -- rendu (appelé par le QTimer de la fenêtre) ------------------------
    def refresh(self) -> None:
        latest = self.store.t_last
        if not latest:
            return
        book = self.store.last_book()

        if self.follow:
            margin = self.x_span * RIGHT_MARGIN_FRAC
            candle_end = math.floor(latest / self.res_s) * self.res_s + self.res_s
            t1 = max(latest, candle_end) + margin
            t0 = t1 - self.x_span
            self.vb.setXRange(t0, t1, padding=0)
            if not self.y_manual and book is not None:
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
        self._vis_price = (float(price.min()), float(price.max()))
        dot = np.clip(2.0 + np.sqrt(size) * DOT_SCALE, 3.0, 26.0)
        self.buys.setData(x=ts[is_buy], y=price[is_buy], size=dot[is_buy])
        self.sells.setData(x=ts[~is_buy], y=price[~is_buy], size=dot[~is_buy])

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
