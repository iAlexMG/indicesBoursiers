# H2 — Croisement SMA 9/21 (5 m) + stop suiveur — JUMEAU BACKTEST de l'hybride H2
# (volet C du chantier automatisation ; specs : automatisation/docs/strategies-hybrides.md).
# Rangé À CÔTÉ des 8 stratégies du banc, qui ne bougent pas. Mécanique : tendance ; ce
# que le live devra prouver : la MODIFICATION d'ordre (le stop suiveur remonté plusieurs
# fois dans un même trade).
# Le jumeau reproduit les DÉCISIONS (entrer, où est le stop, quand il monte) : stop
# SIMULÉ dans la boucle 1 m (patron risque_stops_nq.py) ; l'esprit de
# sma_croisement_nq.py réduit à l'os — le croisement seul, SANS filtre de régime.
#   - Signal : SMA 9/21 sur closes 5 m (seedées par le flux ; le live seede par
#     GetHistory). Croisement -> market ×1 + SL attaché initial à 2 × ATR14 (5 m).
#   - Pas de TP : la sortie naturelle est le stop suiveur ou le croisement inverse.
#   - Suiveur : à chaque clôture 5 m, stop remonté à
#     max(SL courant, extrême favorable depuis l'entrée ∓ 2×ATR14) — ne recule JAMAIS.
#   - Sorties : stop (initial ou suiveur), croisement inverse (market + annulation du
#     stop), ou flat forcé 16:55 ET.
# Cadre commun (cadre_hybride) : entrées 09:30-15:30 ET, garde-fou 2 pertes pleines/jour
# (pour H2 : stop touché SOUS l'entrée — un suiveur pris en gain n'est pas une perte
# pleine), cooldown 15 min, journal de décisions NDJSON.
from AlgorithmImports import *
from datetime import timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL
from cadre_hybride import (CadreSeance, Journal, heure_ny,
                           ENTREE_DEBUT, ENTREE_FIN, FLAT_FORCE, PERTES_MAX)

TF_SIGNAL = 5             # cadence du signal : SMA et ATR sur barres 5 m
SMA_RAPIDE = 9            # SMA courte (closes 5 m)
SMA_LENTE = 21            # SMA longue (closes 5 m)
PERIODE_ATR = 14          # ATR (barres 5 m) du stop initial ET du suiveur
STOP_MULT = 2.0           # stop = extrême favorable ∓ 2 × ATR5m
CONTRATS = 1              # NQ mini ×1, long et short


class SmaSuiveurNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # SIGNAL : SMA 9/21 sur closes 5 m ; ATR 14 sur barres 5 m agrégées.
        self.sma_rapide = SimpleMovingAverage(SMA_RAPIDE)
        self.sma_lente = SimpleMovingAverage(SMA_LENTE)
        self.diff_prec = None
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.o5 = self.h5 = self.l5 = None

        # Cadre de séance + position simulée (stop suiveur dans la boucle) + journal.
        self.cadre = CadreSeance()
        self.journal = Journal("sma_suiveur_nq")
        self.prix_entree = None
        self.stop_prix = None
        self.extreme = None            # extrême favorable depuis l'entrée (suivi 1 m)
        self.atr_entree = None
        self.sortie_en_cours = False   # liquidate envoyé, fill pas encore reçu
        self.raison = ""
        self.id_ordre = 0
        self.id_courant = None

        self.nb_entrees = 0
        self.nb_stop = 0
        self.nb_signal = 0
        self.nb_flat = 0
        self.nb_modifs = 0
        self.nb_garde_fou = 0
        self.frais_totaux = 0.0

    def _sortir(self, t, code, niveau, raison, perte_pleine, evenement="sortie_envoyee"):
        """Sortie simulée : journalise la décision, envoie le market de sortie,
        alimente le garde-fou. Le fill LEAN arrive à la barre suivante."""
        self.raison = code
        if code == "STOP":
            self.nb_stop += 1
        elif code == "SIGNAL":
            self.nb_signal += 1
        elif code == "FLAT":
            self.nb_flat += 1
        self.journal.ecrire(t, evenement, prix=niveau,
                            qte=-int(self.portfolio[self.nq].quantity),
                            id_ordre=self.id_courant, raison=raison)
        self.sortie_en_cours = True
        self.liquidate(self.nq)
        if self.cadre.sortie(t, perte_pleine):
            self.nb_garde_fou += 1
            self.journal.ecrire(t, "garde_fou",
                                raison=f"{PERTES_MAX} pertes pleines — arrêt jusqu'à 09:30 ET")

    def on_data(self, data: Slice):
        if self.nq not in data:
            return
        bar = data[self.nq]
        t = bar.end_time
        close = float(bar.value)
        bas, haut = float(bar["low"]), float(bar["high"])
        t_ny, m = heure_ny(t)

        # Accumuler la barre 5 m (pour l'ATR).
        if self.o5 is None:
            self.o5, self.h5, self.l5 = float(bar["open"]), haut, bas
        else:
            self.h5 = max(self.h5, haut)
            self.l5 = min(self.l5, bas)
        self.cadre.maj_jour(t_ny, m)

        # 1) EN POSITION, chaque 1 m : extrême favorable, stop simulé, flat forcé.
        pos = self.portfolio[self.nq]
        if pos.invested and not self.sortie_en_cours and self.stop_prix is not None:
            self.extreme = (max(self.extreme, haut) if pos.is_long
                            else min(self.extreme, bas))
            if (pos.is_long and bas <= self.stop_prix) or \
               (pos.is_short and haut >= self.stop_prix):
                perte = (self.stop_prix < self.prix_entree if pos.is_long
                         else self.stop_prix > self.prix_entree)
                raison = ("stop touché sous l'entrée (perte pleine)" if perte
                          else "stop suiveur touché (gain/breakeven)")
                self._sortir(t, "STOP", self.stop_prix, raison, perte_pleine=perte)
            elif m >= FLAT_FORCE:
                self.journal.ecrire(t, "annulation", id_ordre=f"{self.id_courant}-sl",
                                    raison="flat forcé : stop annulé")
                self._sortir(t, "FLAT", close, "flat forcé 16:55 ET", perte_pleine=False,
                             evenement="flat_force")

        # 2) SIGNAL aux bornes de 5 min : croisement + trailing du stop.
        if t.minute % TF_SIGNAL != 0:
            return
        self.atr.update(TradeBar(t, self.nq, self.o5, self.h5, self.l5, close, 0.0,
                                 timedelta(minutes=TF_SIGNAL)))
        self.sma_rapide.update(t, close)
        self.sma_lente.update(t, close)
        self.o5 = self.h5 = self.l5 = None
        if not (self.sma_rapide.is_ready and self.sma_lente.is_ready and self.atr.is_ready):
            return
        rapide = float(self.sma_rapide.current.value)
        lente = float(self.sma_lente.current.value)
        atr = float(self.atr.current.value)
        diff = rapide - lente
        croise_haut = self.diff_prec is not None and self.diff_prec <= 0 and diff > 0
        croise_bas = self.diff_prec is not None and self.diff_prec >= 0 and diff < 0
        self.diff_prec = diff

        pos = self.portfolio[self.nq]           # relire (le stop a pu liquider)
        if pos.invested and not self.sortie_en_cours and self.prix_entree is not None:
            # a) Croisement inverse -> sortie signal + annulation du stop.
            if (pos.is_long and croise_bas) or (pos.is_short and croise_haut):
                self.journal.ecrire(t, "signal", prix=close,
                                    raison="croisement inverse -> sortie",
                                    sma_rapide=rapide, sma_lente=lente, atr=atr)
                self.journal.ecrire(t, "annulation", id_ordre=f"{self.id_courant}-sl",
                                    raison="sortie signal : stop annulé")
                self._sortir(t, "SIGNAL", close, "croisement inverse", perte_pleine=False)
            # b) Sinon, stop suiveur : extrême favorable ∓ 2×ATR, ne recule jamais.
            elif pos.is_long:
                candidat = self.extreme - STOP_MULT * atr
                if candidat > self.stop_prix:
                    self.stop_prix = candidat
                    self.nb_modifs += 1
                    self.journal.ecrire(t, "stop_modifie", prix=self.stop_prix,
                                        id_ordre=f"{self.id_courant}-sl",
                                        raison="suiveur remonté",
                                        extreme=self.extreme, atr=atr)
            else:
                candidat = self.extreme + STOP_MULT * atr
                if candidat < self.stop_prix:
                    self.stop_prix = candidat
                    self.nb_modifs += 1
                    self.journal.ecrire(t, "stop_modifie", prix=self.stop_prix,
                                        id_ordre=f"{self.id_courant}-sl",
                                        raison="suiveur descendu",
                                        extreme=self.extreme, atr=atr)
        elif not pos.invested and not self.sortie_en_cours and (croise_haut or croise_bas):
            sens = CONTRATS if croise_haut else -CONTRATS
            sens_txt = "haussier" if croise_haut else "baissier"
            refus = None
            if not (ENTREE_DEBUT < m <= ENTREE_FIN):
                refus = "hors fenêtre d'entrée (09:30-15:30 ET)"
            elif self.cadre.garde_fou:
                refus = "garde-fou journalier actif"
            elif not self.cadre.cooldown_ok(t):
                refus = "cooldown 15 min"
            if refus:
                self.journal.ecrire(t, "signal", prix=close,
                                    raison=f"croisement {sens_txt} REFUSÉ : {refus}",
                                    sma_rapide=rapide, sma_lente=lente, atr=atr)
                return
            self.journal.ecrire(t, "signal", prix=close,
                                raison=f"croisement {sens_txt} -> entrée",
                                sma_rapide=rapide, sma_lente=lente, atr=atr)
            self.atr_entree = atr
            self.id_ordre += 1
            self.id_courant = self.id_ordre
            self.journal.ecrire(t, "entree_envoyee", prix=close, qte=sens,
                                id_ordre=self.id_courant,
                                raison="market ×1 sur croisement confirmé")
            viser(self, self.nq, sens)

    def on_order_event(self, event: OrderEvent):
        if event.status != OrderStatus.FILLED:
            return
        self.frais_totaux += float(event.order_fee.value.amount)
        t = event.utc_time
        pos = self.portfolio[self.nq]
        if pos.invested:
            # Fill d'entrée : SL initial posé, extrême favorable amorcé au fill.
            e = float(event.fill_price)
            self.prix_entree = e
            self.extreme = e
            self.nb_entrees += 1
            self.stop_prix = (e - STOP_MULT * self.atr_entree if pos.is_long
                              else e + STOP_MULT * self.atr_entree)
            self.journal.ecrire(t, "fill", prix=e, qte=int(event.fill_quantity),
                                id_ordre=self.id_courant, raison="fill d'entrée")
            self.journal.ecrire(t, "bracket_pose", prix=e,
                                id_ordre=f"{self.id_courant}-sl",
                                raison=f"SL seul {STOP_MULT}×ATR, pas de TP "
                                       f"(simulé, boucle 1 m)",
                                stop=self.stop_prix, atr=self.atr_entree)
            sens = "ACHAT " if event.fill_quantity > 0 else "VENTE "
            self.log(f"ENTRÉE {self.nb_entrees:>3} {sens}{t:%Y-%m-%d %H:%M} UTC | "
                     f"ATR={self.atr_entree:.1f} | @ {e} | stop={self.stop_prix:.2f}")
        else:
            s = float(event.fill_price)
            self.journal.ecrire(t, "fill", prix=s, qte=int(event.fill_quantity),
                                id_ordre=self.id_courant,
                                raison=f"fill de sortie [{self.raison}]")
            etait_long = event.fill_quantity < 0
            gain = ((s / self.prix_entree - 1) if etait_long
                    else (self.prix_entree / s - 1)) if self.prix_entree else 0.0
            self.log(f"SORTIE {t:%Y-%m-%d %H:%M} UTC | [{self.raison}] @ {s} | "
                     f"P&L={gain:+.2%}")
            self.prix_entree = self.stop_prix = self.extreme = None
            self.sortie_en_cours = False

    def on_end_of_algorithm(self):
        self.journal.fermer()
        equite = float(self.portfolio.total_portfolio_value)
        self.log(f"--- BILAN JUMEAU H2 SMA {SMA_RAPIDE}/{SMA_LENTE} + SUIVEUR NQ "
                 f"(signal 5 m, stop {STOP_MULT}×ATR{PERIODE_ATR}, sans régime, "
                 f"flat 16:55 ET) ---")
        self.log(f"Entrées : {self.nb_entrees} | sorties : {self.nb_stop} stop, "
                 f"{self.nb_signal} croisement inverse, {self.nb_flat} flat forcé | "
                 f"stop modifié {self.nb_modifs} fois | garde-fou : {self.nb_garde_fou} | "
                 f"frais : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement : {equite / CAPITAL - 1:+.4%}")
        self.log(f"Journal de décisions : {self.journal.dossier}")
