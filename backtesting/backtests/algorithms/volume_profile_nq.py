# Volume profile PAR SESSION + analyse des volumes sur NQ — SCALPING 1 m, long/short
# (miroir de volume_profile.py du frère crypto, re-calibrage du 2026-07-17).
# DEUX flux de données custom dans le même algo :
#   - le prix (1m.csv via nq_instrument, lecteur validé) ;
#   - les features de profil (features_vp.csv : session, barres, delta, POC/VAH/VAL du
#     profil de la session EN COURS — Asia/London/NY, heure de New York — développé
#     MINUTE PAR MINUTE), reconstruites par backtests/volume_profile_features.py
#     depuis les ticks NQ (niveaux de 5 pts).
# Lecture retenue : ACCEPTATION SYMÉTRIQUE de la value area de LA SESSION —
#   - cassure du HAUT (VAH) par le haut + delta acheteur -> LONG  (prix acceptés au-dessus) ;
#   - cassure du BAS (VAL) par le bas + delta vendeur    -> SHORT (prix acceptés en dessous).
# Le profil fournit lui-même la cible et le stop (dérivés des niveaux, pas des % fixes).
# Règles de session (a priori) : pas d'entrée hors session ni dans les MIN_BARRES premières
# minutes d'une session (profil embryonnaire) ; pas de croisement détecté À CHEVAL sur deux
# enchères (les niveaux sautent au reset). Long/short.
# Leviers anti-churn du frère conservés : MIN_BARRES 15, COOLDOWN 45, DELTA_SPAN 60.
# ⚠️ MIN_EDGE reste à 0,2 % (au lieu du 0,4 % du frère) : le doublement crypto répondait
# aux frais PROPORTIONNELS (0,04 % du notionnel) ; les frais NQ sont FIXES (~2 $/side,
# ~0,001 % du notionnel) — une différence d'instrument, pas un réglage sur la courbe.
from AlgorithmImports import *
from datetime import datetime, timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL

VP_FILE = "H:/IndicesBoursiers/historique/ohlcv/NQ-2026-09/features_vp.csv"

# ── Paramètres de la stratégie (choisis A PRIORI — jamais après la courbe)
FILTRE_DELTA = True      # n'entrer que si l'EMA du delta va dans le sens du trade
DELTA_SPAN = 60          # période de l'EMA du delta (60 barres = 60 min en cadence 1 m)
TP_FRAC = 1.0            # cible : fraction du chemin niveau->projection (1.0 = chemin entier)
STOP_FRAC = 0.5          # stop : fraction du chemin AU-DELÀ du niveau (retour = hypothèse morte)
MIN_EDGE = 0.002         # edge minimal 0,2 % du chemin niveau->cible (frais fixes NQ, cf. header)
MIN_BARRES = 15          # pas d'entrée avant la 15e MINUTE d'une session (profil embryonnaire)
COOLDOWN_MIN = 45        # pas de nouvelle entrée dans les 45 min après une sortie
CONTRATS = 1             # granularité futures : 1 contrat NQ


class VpFeatures(PythonData):
    """Second flux : la ligne t porte le profil de la session en cours, développé de
    l'ouverture de session à la clôture de la MINUTE t (incluse).
    end_time = t+1min -> LEAN la livre à la clôture de la barre t, dans le MÊME Slice
    que la barre de prix t : causal par construction.
    Colonnes : time,session,barres,delta,poc,vah,val"""

    def get_source(self, config, date, is_live):
        return SubscriptionDataSource(VP_FILE, SubscriptionTransportMedium.LOCAL_FILE)

    def reader(self, config, line, date, is_live):
        if not line or not line[0].isdigit():
            return None
        cols = line.split(",")
        if cols[4] == "":                 # tout début d'historique : aucun profil encore
            return None
        bar = VpFeatures()
        bar.symbol = config.symbol
        t_open = datetime.strptime(cols[0][:19], "%Y-%m-%d %H:%M:%S")
        bar.time = t_open
        bar.end_time = t_open + timedelta(minutes=1)
        bar.value = float(cols[4])        # POC (value obligatoire, jamais de NaN)
        bar["session"] = cols[1]          # asia | london | ny | hors
        bar["barres"] = float(cols[2])    # ancienneté du profil dans la session (minutes)
        bar["delta"] = float(cols[3])
        bar["poc"] = float(cols[4])
        bar["vah"] = float(cols[5])
        bar["val"] = float(cols[6])
        return bar


class VolumeProfileNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # Le flux de features n'est PAS un instrument tradé, mais il lui faut le même
        # cadre 24/7 UTC que le prix pour que ses timestamps soient lus en UTC.
        props_vp = SymbolProperties("Features volume profile NQ", "USD", 1, 0.25, 1, "NQVP")
        self.vp = self.add_data(VpFeatures, "NQVP", props_vp,
                                SecurityExchangeHours.always_open(TimeZones.UTC),
                                Resolution.MINUTE).symbol

        # EMA du delta tenue à la main (lissage standard 2/(N+1)) : l'analyse des volumes.
        # ⚠️ ne PAS l'appeler self.alpha : `alpha` est une propriété .NET de QCAlgorithm
        # (le modèle alpha du framework) — l'affectation planterait initialize.
        self.lissage = 2.0 / (DELTA_SPAN + 1)
        self.delta_ema = None

        self.poc = self.vah = self.val = None   # niveaux courants du profil de session
        self.session = None          # session active (asia | london | ny | hors)
        self.barres = 0              # ancienneté du profil dans la session (minutes)
        self.close_prec = None
        self.vah_prec = None
        self.val_prec = None
        self.temps_sortie = None     # horodatage de la dernière sortie (pour le cooldown)
        self.nb_trades = 0
        self.frais_totaux = 0.0
        self.premier_close = None
        self.dernier_close = None

    def _cooldown_ok(self, maintenant):
        return (self.temps_sortie is None
                or (maintenant - self.temps_sortie).total_seconds() >= COOLDOWN_MIN * 60)

    def on_data(self, data: Slice):
        # 1) Le profil d'abord : rafraîchir session, niveaux et EMA du delta.
        if self.vp in data:
            f = data[self.vp]
            if str(f["session"]) != self.session:
                # Nouvelle enchère : les niveaux SAUTENT (profil remis à zéro) -> on ne
                # détecte jamais un « franchissement » à cheval sur deux sessions.
                self.session = str(f["session"])
                self.close_prec = None
                self.vah_prec = None
                self.val_prec = None
            self.barres = int(float(f["barres"]))
            self.poc, self.vah, self.val = float(f["poc"]), float(f["vah"]), float(f["val"])
            delta = float(f["delta"])
            self.delta_ema = delta if self.delta_ema is None else \
                self.lissage * delta + (1 - self.lissage) * self.delta_ema

        # 2) Puis le prix : signaux au close de la barre clôturée.
        if self.nq not in data:
            return
        close = float(data[self.nq].value)
        if self.premier_close is None:
            self.premier_close = close        # AVANT le warmup : trace de référence
        self.dernier_close = close
        if self.poc is None:
            return                            # warmup du profil
        poc, vah, val = self.poc, self.vah, self.val
        pos = self.portfolio[self.nq]

        if not pos.invested:
            # Entrée : le close FRANCHIT un bord de la value area (événement), DANS une
            # session dont le profil a au moins MIN_BARRES minutes, l'edge du chemin
            # couvre les frais, et les agresseurs vont dans le sens du trade.
            session_ok = (self.session != "hors" and self.barres >= MIN_BARRES
                          and self._cooldown_ok(self.time))
            if session_ok and self.close_prec is not None:
                # LONG : cassure de VAH par le haut (acceptation au-dessus de la valeur).
                if (self.vah_prec is not None and self.close_prec <= self.vah_prec
                        and close > vah):
                    amp = vah - poc                       # projection = demi-largeur haute
                    cible = vah + TP_FRAC * amp
                    edge_ok = amp > 0 and (cible - vah) / vah >= MIN_EDGE
                    delta_ok = (not FILTRE_DELTA) or (self.delta_ema is not None
                                                      and self.delta_ema > 0)
                    if edge_ok and delta_ok:
                        viser(self, self.nq, CONTRATS)
                        self.log(f"ENTREE LONG  {self.time:%Y-%m-%d %H:%M} [{self.session} "
                                 f"m{self.barres}] | close={close} > vah={vah:.0f} | "
                                 f"cible={cible:.0f} | delta_ema={self.delta_ema:+.0f}")
                # SHORT : cassure de VAL par le bas (acceptation en dessous de la valeur).
                elif (self.val_prec is not None and self.close_prec >= self.val_prec
                        and close < val):
                    amp = poc - val
                    cible = val - TP_FRAC * amp
                    edge_ok = amp > 0 and (val - cible) / val >= MIN_EDGE
                    delta_ok = (not FILTRE_DELTA) or (self.delta_ema is not None
                                                      and self.delta_ema < 0)
                    if edge_ok and delta_ok:
                        viser(self, self.nq, -CONTRATS)
                        self.log(f"ENTREE SHORT {self.time:%Y-%m-%d %H:%M} [{self.session} "
                                 f"m{self.barres}] | close={close} < val={val:.0f} | "
                                 f"cible={cible:.0f} | delta_ema={self.delta_ema:+.0f}")
        else:
            # Sorties sur les niveaux COURANTS du profil (externes à la position) :
            # cible atteinte, ou retour au-delà du niveau conquis (hypothèse invalidée).
            if pos.is_long:
                amp = vah - poc
                cible = vah + TP_FRAC * amp
                stop = vah - STOP_FRAC * amp
                if close >= cible:
                    self.liquidate(self.nq); self.temps_sortie = self.time
                    self.log(f"SORTIE cible {self.time:%Y-%m-%d %H:%M} | close={close} >= {cible:.0f}")
                elif close <= stop:
                    self.liquidate(self.nq); self.temps_sortie = self.time
                    self.log(f"SORTIE stop  {self.time:%Y-%m-%d %H:%M} | close={close} <= {stop:.0f}")
            else:                                          # short
                amp = poc - val
                cible = val - TP_FRAC * amp
                stop = val + STOP_FRAC * amp
                if close <= cible:
                    self.liquidate(self.nq); self.temps_sortie = self.time
                    self.log(f"SORTIE cible {self.time:%Y-%m-%d %H:%M} | close={close} <= {cible:.0f}")
                elif close >= stop:
                    self.liquidate(self.nq); self.temps_sortie = self.time
                    self.log(f"SORTIE stop  {self.time:%Y-%m-%d %H:%M} | close={close} >= {stop:.0f}")

        self.close_prec, self.vah_prec, self.val_prec = close, vah, val

    def on_order_event(self, event: OrderEvent):
        if event.status == OrderStatus.FILLED:
            self.nb_trades += 1
            self.frais_totaux += float(event.order_fee.value.amount)
            sens = "ACHAT " if event.fill_quantity > 0 else "VENTE "
            self.log(f"TRADE {self.nb_trades:>3} {sens}{event.utc_time:%Y-%m-%d %H:%M} UTC | "
                     f"qté={event.fill_quantity:+.0f} contrat(s) @ {event.fill_price} | "
                     f"frais={event.order_fee.value.amount:.2f} $")

    def on_end_of_algorithm(self):
        equite = float(self.portfolio.total_portfolio_value)
        rendement = equite / CAPITAL - 1
        self.log(f"--- BILAN Volume profile NQ 1 m (acceptation VAH/VAL, "
                 f"filtre delta={FILTRE_DELTA}), long/short ---")
        self.log(f"Trades exécutés : {self.nb_trades} | frais totaux : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement stratégie : {rendement:+.4%}")
