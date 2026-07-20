# H1 — Cassure de plage d'ouverture (ORB) — JUMEAU BACKTEST de l'hybride H1 (volet C du
# chantier automatisation ; specs : automatisation/docs/strategies-hybrides.md). Rangé À
# CÔTÉ des 8 stratégies du banc, qui ne bougent pas. Mécanique : cassure ; ce que le
# live devra prouver : le BRACKET posé au fill.
# Le jumeau reproduit les DÉCISIONS (quand entrer, où sont SL/TP) : bracket SIMULÉ dans
# la boucle 1 m sur extrêmes intra-barre — le patron de risque_stops_nq.py. La mécanique
# d'ordres réelle, elle, se prouve en live (répartition des rôles). Rappel du cadrage :
# référence de décisions et validation de logique, PAS un verdict de performance.
#   - Plage : plus-haut/plus-bas de 09:30 -> 10:00 ET (barres 1 m).
#   - Entrée : première clôture 1 m au-delà d'une borne, 10:00 -> 12:00 ET, AU PLUS UNE
#     entrée par jour -> market ×1 + bracket SL/TP. Pas de cassure avant midi = pas de
#     trade du jour.
#   - SL = 1,5 × ATR14 (barres 5 m, valeur à l'entrée) | TP = 2R (= entrée ± 3 × ATR).
#   - Sorties : TP, SL ou flat forcé 16:55 ET — pas de stop suiveur. Après sortie,
#     terminé pour la journée.
# Cadre commun (cadre_hybride) : garde-fou et cooldown gardés pour l'uniformité (sans
# effet ici — une seule entrée par jour) + journal de décisions NDJSON.
from AlgorithmImports import *
from datetime import timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL
from cadre_hybride import (CadreSeance, Journal, heure_ny,
                           ENTREE_DEBUT, FLAT_FORCE, PERTES_MAX)

PLAGE_MIN = 30            # plage d'ouverture : 09:30 -> 10:00 ET (variante 15 min possible)
FENETRE_FIN = 12 * 60     # 12:00 ET — fin de la fenêtre d'entrée
TF_ATR = 5                # cadence de l'ATR : barres 5 m agrégées
PERIODE_ATR = 14          # ATR qui dimensionne le stop
STOP_MULT = 1.5           # SL = entrée ∓ 1,5 × ATR5m (= R)
TP_R = 2.0                # TP = entrée ± 2 R
CONTRATS = 1              # NQ mini ×1, long et short


class OrbNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # ATR de dimensionnement, sur barres 5 m agrégées (accumulateur OHLC 5 min).
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.o5 = self.h5 = self.l5 = None

        # Plage d'ouverture + droit à UNE entrée, remis à zéro au changement de jour ET.
        self.jour = None
        self.borne_haute = None
        self.borne_basse = None
        self.entree_faite = False

        # Cadre de séance + position simulée (bracket dans la boucle) + journal.
        self.cadre = CadreSeance()
        self.journal = Journal("orb_nq")
        self.prix_entree = None
        self.stop_prix = None
        self.take_prix = None
        self.atr_entree = None
        self.sortie_en_cours = False   # liquidate envoyé, fill pas encore reçu
        self.raison = ""
        self.id_ordre = 0
        self.id_courant = None

        self.nb_entrees = 0
        self.nb_stop = 0
        self.nb_take = 0
        self.nb_flat = 0
        self.frais_totaux = 0.0

    def _sortir(self, t, code, niveau, raison, perte_pleine, evenement="sortie_envoyee"):
        """Sortie simulée : journalise la décision, envoie le market de sortie,
        alimente le garde-fou. Le fill LEAN arrive à la barre suivante."""
        self.raison = code
        if code == "SL":
            self.nb_stop += 1
        elif code == "TP":
            self.nb_take += 1
        elif code == "FLAT":
            self.nb_flat += 1
        self.journal.ecrire(t, evenement, prix=niveau,
                            qte=-int(self.portfolio[self.nq].quantity),
                            id_ordre=self.id_courant, raison=raison)
        self.sortie_en_cours = True
        self.liquidate(self.nq)
        if self.cadre.sortie(t, perte_pleine):
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

        # Accumuler la barre 5 m et nourrir l'ATR aux bornes de 5 min.
        if self.o5 is None:
            self.o5, self.h5, self.l5 = float(bar["open"]), haut, bas
        else:
            self.h5 = max(self.h5, haut)
            self.l5 = min(self.l5, bas)
        if t.minute % TF_ATR == 0:
            self.atr.update(TradeBar(t, self.nq, self.o5, self.h5, self.l5, close, 0.0,
                                     timedelta(minutes=TF_ATR)))
            self.o5 = self.h5 = self.l5 = None

        # Nouveau jour ET : plage à reconstruire, droit à une nouvelle entrée.
        if t_ny.date() != self.jour:
            self.jour = t_ny.date()
            self.borne_haute = self.borne_basse = None
            self.entree_faite = False
        self.cadre.maj_jour(t_ny, m)

        # 1) EN POSITION : bracket simulé (SL prioritaire), puis flat forcé — chaque 1 m.
        pos = self.portfolio[self.nq]
        if pos.invested and not self.sortie_en_cours and self.stop_prix is not None:
            touche = None
            if pos.is_long:
                if bas <= self.stop_prix:
                    touche, niveau = "SL", self.stop_prix
                elif haut >= self.take_prix:
                    touche, niveau = "TP", self.take_prix
            else:
                if haut >= self.stop_prix:
                    touche, niveau = "SL", self.stop_prix
                elif bas <= self.take_prix:
                    touche, niveau = "TP", self.take_prix
            if touche:
                self._sortir(t, touche, niveau, f"{touche} touché (bracket simulé)",
                             perte_pleine=(touche == "SL"))
            elif m >= FLAT_FORCE:
                self.journal.ecrire(t, "annulation", id_ordre=f"{self.id_courant}-bracket",
                                    raison="flat forcé : bracket annulé")
                self._sortir(t, "FLAT", close, "flat forcé 16:55 ET", perte_pleine=False,
                             evenement="flat_force")

        # 2) Fenêtre de plage : construire les bornes (barres closes entre 09:30 et 10:00).
        if ENTREE_DEBUT < m <= ENTREE_DEBUT + PLAGE_MIN:
            self.borne_haute = haut if self.borne_haute is None else max(self.borne_haute, haut)
            self.borne_basse = bas if self.borne_basse is None else min(self.borne_basse, bas)
            return

        # 3) ENTRÉE : première clôture 1 m au-delà d'une borne, 10:00 -> 12:00 ET.
        pos = self.portfolio[self.nq]           # relire (le bracket a pu liquider)
        if (self.entree_faite or pos.invested or self.sortie_en_cours
                or self.borne_haute is None or not self.atr.is_ready
                or not (ENTREE_DEBUT + PLAGE_MIN < m <= FENETRE_FIN)
                or self.cadre.garde_fou or not self.cadre.cooldown_ok(t)):
            return
        sens = 0
        if close > self.borne_haute:
            sens = CONTRATS
        elif close < self.borne_basse:
            sens = -CONTRATS
        if sens == 0:
            return

        self.entree_faite = True
        self.atr_entree = float(self.atr.current.value)
        self.id_ordre += 1
        self.id_courant = self.id_ordre
        sens_txt = "long" if sens > 0 else "short"
        self.journal.ecrire(t, "signal", prix=close,
                            raison=f"première clôture 1 m au-delà de la borne -> {sens_txt}",
                            borne_haute=self.borne_haute, borne_basse=self.borne_basse,
                            atr=self.atr_entree)
        self.journal.ecrire(t, "entree_envoyee", prix=close, qte=sens,
                            id_ordre=self.id_courant, raison="market ×1 sur cassure confirmée")
        viser(self, self.nq, sens)

    def on_order_event(self, event: OrderEvent):
        if event.status != OrderStatus.FILLED:
            return
        self.frais_totaux += float(event.order_fee.value.amount)
        t = event.utc_time
        pos = self.portfolio[self.nq]
        if pos.invested:
            # Fill d'entrée : bracket posé à partir de l'ATR gelé à l'instant du signal.
            e = float(event.fill_price)
            self.prix_entree = e
            self.nb_entrees += 1
            r = STOP_MULT * self.atr_entree
            if pos.is_long:
                self.stop_prix, self.take_prix = e - r, e + TP_R * r
            else:
                self.stop_prix, self.take_prix = e + r, e - TP_R * r
            self.journal.ecrire(t, "fill", prix=e, qte=int(event.fill_quantity),
                                id_ordre=self.id_courant, raison="fill d'entrée")
            self.journal.ecrire(t, "bracket_pose", prix=e,
                                id_ordre=f"{self.id_courant}-bracket",
                                raison=f"SL {STOP_MULT}×ATR / TP {TP_R}R (simulé, boucle 1 m)",
                                stop=self.stop_prix, take=self.take_prix, atr=self.atr_entree)
            sens = "ACHAT " if event.fill_quantity > 0 else "VENTE "
            self.log(f"ENTRÉE {self.nb_entrees:>3} {sens}{t:%Y-%m-%d %H:%M} UTC | "
                     f"plage [{self.borne_basse:.2f} ; {self.borne_haute:.2f}] "
                     f"ATR={self.atr_entree:.1f} | @ {e} | "
                     f"stop={self.stop_prix:.2f} take={self.take_prix:.2f}")
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
            self.prix_entree = self.stop_prix = self.take_prix = None
            self.sortie_en_cours = False

    def on_end_of_algorithm(self):
        self.journal.fermer()
        equite = float(self.portfolio.total_portfolio_value)
        self.log(f"--- BILAN JUMEAU H1 ORB NQ (plage 09:30-10:00 ET, entrée 10:00-12:00, "
                 f"SL {STOP_MULT}×ATR{PERIODE_ATR} 5 m, TP {TP_R}R, flat 16:55 ET) ---")
        self.log(f"Entrées : {self.nb_entrees} | sorties : {self.nb_stop} SL, "
                 f"{self.nb_take} TP, {self.nb_flat} flat forcé | "
                 f"frais : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement : {equite / CAPITAL - 1:+.4%}")
        self.log(f"Journal de décisions : {self.journal.dossier}")
