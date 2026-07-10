# Volume profile PAR SESSION (vah_break + filtre delta EMA24) — porte sur NQ.
# Niveaux de profil recalibres a 5 pts NQ (20 ticks) au lieu de 25 $ BTC (features_vp NQ).
# Logique identique au frere ; sizing en contrats entiers.
from AlgorithmImports import *
from datetime import datetime, timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, CAPITAL

VP_FILE = "F:/data/ohlcv/NQ-2026-09/features_vp.csv"
LECTURE = "vah_break"
FILTRE_DELTA = True
DELTA_SPAN = 24
TP_FRAC = 1.0
STOP_FRAC = 0.5
MIN_EDGE = 0.002
MIN_BARRES = 3
CONTRATS = 1


class VpFeatures(PythonData):
    """Flux features VP : time,session,barres,delta,poc,vah,val (causal, livre a t+1h)."""

    def get_source(self, config, date, is_live):
        return SubscriptionDataSource(VP_FILE, SubscriptionTransportMedium.LOCAL_FILE)

    def reader(self, config, line, date, is_live):
        if not line or not line[0].isdigit():
            return None
        cols = line.split(",")
        if cols[4] == "":
            return None
        bar = VpFeatures()
        bar.symbol = config.symbol
        t_open = datetime.strptime(cols[0][:19], "%Y-%m-%d %H:%M:%S")
        bar.time = t_open
        bar.end_time = t_open + timedelta(hours=1)
        bar.value = float(cols[4])
        bar["session"] = cols[1]
        bar["barres"] = float(cols[2])
        bar["delta"] = float(cols[3])
        bar["poc"] = float(cols[4])
        bar["vah"] = float(cols[5])
        bar["val"] = float(cols[6])
        return bar


class VolumeProfileNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        props_vp = SymbolProperties("Features VP NQ", "USD", 1, 0.25, 1, "NQVP")
        self.vp = self.add_data(VpFeatures, "NQVP", props_vp,
                                SecurityExchangeHours.always_open(TimeZones.UTC),
                                Resolution.HOUR).symbol
        self.lissage = 2.0 / (DELTA_SPAN + 1)
        self.delta_ema = None
        self.niveaux = None
        self.session = None
        self.barres = 0
        self.close_prec = None
        self.lo_prec = None
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.premier_close = None
        self.dernier_close = None

    def on_data(self, data: Slice):
        if self.vp in data:
            f = data[self.vp]
            if str(f["session"]) != self.session:
                self.session = str(f["session"])
                self.close_prec = None
                self.lo_prec = None
            self.barres = int(float(f["barres"]))
            poc, vah, val = float(f["poc"]), float(f["vah"]), float(f["val"])
            delta = float(f["delta"])
            self.delta_ema = delta if self.delta_ema is None else \
                self.lissage * delta + (1 - self.lissage) * self.delta_ema
            if LECTURE == "vah_break":
                lo, hi = vah, vah + (vah - poc)
            else:
                lo, hi = val, poc
            self.niveaux = (lo, lo + TP_FRAC * (hi - lo), lo - STOP_FRAC * (hi - lo))

        if self.nq not in data:
            return
        close = float(data[self.nq].value)
        if self.premier_close is None:
            self.premier_close = close
        self.dernier_close = close
        if self.niveaux is None:
            return
        lo, cible, stop = self.niveaux

        if not self.portfolio[self.nq].invested:
            franchit = (self.close_prec is not None and self.lo_prec is not None
                        and self.close_prec <= self.lo_prec and close > lo)
            session_ok = self.session != "hors" and self.barres >= MIN_BARRES
            edge_ok = (cible - lo) / lo >= MIN_EDGE
            delta_ok = (not FILTRE_DELTA) or (self.delta_ema is not None and self.delta_ema > 0)
            if franchit and session_ok and edge_ok and delta_ok:
                self.market_order(self.nq, CONTRATS)
        else:
            if close >= cible:
                self.liquidate(self.nq)
            elif close <= stop:
                self.liquidate(self.nq)

        self.close_prec, self.lo_prec = close, lo

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        self.log(f"--- BILAN Volume profile NQ ({LECTURE}, filtre delta={FILTRE_DELTA}) ---")
        self.log(f"Trades : {self.nb_trades} | frais : {self.frais_totaux:.2f} $")
        self.log(f"Equite finale : {equite:.2f} $ | rendement : {rendement:+.4%}")
