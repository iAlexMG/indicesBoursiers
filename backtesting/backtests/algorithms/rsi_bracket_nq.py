# H3 — RSI 9 retour à la moyenne + bracket — JUMEAU BACKTEST de l'hybride H3 (volet C
# du chantier automatisation ; specs : automatisation/docs/strategies-hybrides.md).
# Rangé À CÔTÉ des 8 stratégies du banc, qui ne bougent pas. Mécanique :
# contre-tendance ; ce que le live devra prouver : l'ANNULATION du bracket sur sortie
# signal (RSI revenu à ~50).
# Le jumeau reproduit les DÉCISIONS : bracket SIMULÉ dans la boucle 1 m (patron
# risque_stops_nq.py) ; l'esprit de rsi_retour_moyenne_nq.py SANS filtre de régime — le
# fade se prend des deux bords, le bracket encadre tout.
#   - Signal : RSI 9 (Wilder) aux bornes de 3 m — franchissement sous 30 -> long,
#     au-dessus de 70 -> short. Market ×1 + bracket SL/TP complet.
#   - SL = 1,5 × ATR14 (barres 3 m) | TP = 1R (cible courte assumée — retour à la
#     moyenne ; ratio 1R vs 1,5R à confirmer par l'utilisateur).
#   - Sortie anticipée : RSI revenu à ~50 -> market + ANNULATION du bracket.
#   - Sorties : TP, SL, RSI 50, ou flat forcé 16:55 ET.
# Cadre commun (cadre_hybride) : entrées 09:30-15:30 ET, garde-fou 2 pertes
# pleines/jour (SL touché = perte pleine), cooldown 15 min, journal NDJSON.
from AlgorithmImports import *
from datetime import timedelta
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL
from cadre_hybride import (CadreSeance, Journal, heure_ny,
                           ENTREE_DEBUT, ENTREE_FIN, FLAT_FORCE, PERTES_MAX)

TF_SIGNAL = 3             # cadence du signal : RSI et ATR sur barres 3 m
PERIODE_RSI = 9           # RSI court (Wilder)
SURVENTE = 30             # franchissement SOUS 30 -> long
SURACHAT = 70             # franchissement AU-DESSUS de 70 -> short
MOYENNE = 50              # RSI revenu à ~50 -> sortie anticipée + annulation
PERIODE_ATR = 14          # ATR (barres 3 m) qui dimensionne le bracket
STOP_MULT = 1.5           # SL = entrée ∓ 1,5 × ATR3m (= R)
TP_R = 1.0                # TP = entrée ± 1 R (défaut, 1,5R à confirmer)
CONTRATS = 1              # NQ mini ×1, long et short


class RsiBracketNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)

        # SIGNAL : RSI 9 sur closes 3 m ; ATR 14 sur barres 3 m agrégées.
        self.rsi = RelativeStrengthIndex(PERIODE_RSI, MovingAverageType.WILDERS)
        self.rsi_prec = None
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.o3 = self.h3 = self.l3 = None

        # Cadre de séance + position simulée (bracket dans la boucle) + journal.
        self.cadre = CadreSeance()
        self.journal = Journal("rsi_bracket_nq")
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
        self.nb_signal = 0
        self.nb_flat = 0
        self.nb_garde_fou = 0
        self.frais_totaux = 0.0

    def _sortir(self, t, code, niveau, raison, perte_pleine, evenement="sortie_envoyee"):
        """Sortie simulée : journalise la décision, envoie le market de sortie,
        alimente le garde-fou. Le fill LEAN arrive à la barre suivante."""
        self.raison = code
        if code == "SL":
            self.nb_stop += 1
        elif code == "TP":
            self.nb_take += 1
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

        # Accumuler la barre 3 m (pour l'ATR).
        if self.o3 is None:
            self.o3, self.h3, self.l3 = float(bar["open"]), haut, bas
        else:
            self.h3 = max(self.h3, haut)
            self.l3 = min(self.l3, bas)
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

        # 2) SIGNAL : RSI + ATR aux bornes de 3 min.
        if t.minute % TF_SIGNAL != 0:
            return
        self.atr.update(TradeBar(t, self.nq, self.o3, self.h3, self.l3, close, 0.0,
                                 timedelta(minutes=TF_SIGNAL)))
        self.rsi.update(t, close)
        self.o3 = self.h3 = self.l3 = None
        if not (self.rsi.is_ready and self.atr.is_ready):
            return
        rsi = float(self.rsi.current.value)
        atr = float(self.atr.current.value)
        pos = self.portfolio[self.nq]           # relire (le bracket a pu liquider)

        if pos.invested and not self.sortie_en_cours and self.prix_entree is not None:
            # Sortie anticipée : RSI revenu à ~50 -> market + annulation du bracket.
            if (pos.is_long and rsi >= MOYENNE) or (pos.is_short and rsi <= MOYENNE):
                self.journal.ecrire(t, "signal", prix=close,
                                    raison=f"RSI revenu à {MOYENNE} -> sortie anticipée",
                                    rsi=rsi, atr=atr)
                self.journal.ecrire(t, "annulation", id_ordre=f"{self.id_courant}-bracket",
                                    raison="sortie signal : bracket annulé")
                self._sortir(t, "SIGNAL", close, f"RSI revenu à {MOYENNE}",
                             perte_pleine=False)
        elif (not pos.invested and not self.sortie_en_cours
                and self.rsi_prec is not None):
            entre_survente = self.rsi_prec >= SURVENTE and rsi < SURVENTE
            entre_surachat = self.rsi_prec <= SURACHAT and rsi > SURACHAT
            if entre_survente or entre_surachat:
                sens = CONTRATS if entre_survente else -CONTRATS
                sens_txt = (f"franchissement sous {SURVENTE} -> long" if entre_survente
                            else f"franchissement au-dessus de {SURACHAT} -> short")
                refus = None
                if not (ENTREE_DEBUT < m <= ENTREE_FIN):
                    refus = "hors fenêtre d'entrée (09:30-15:30 ET)"
                elif self.cadre.garde_fou:
                    refus = "garde-fou journalier actif"
                elif not self.cadre.cooldown_ok(t):
                    refus = "cooldown 15 min"
                if refus:
                    self.journal.ecrire(t, "signal", prix=close,
                                        raison=f"{sens_txt} — REFUSÉ : {refus}",
                                        rsi=rsi, atr=atr)
                else:
                    self.journal.ecrire(t, "signal", prix=close, raison=sens_txt,
                                        rsi=rsi, atr=atr)
                    self.atr_entree = atr
                    self.id_ordre += 1
                    self.id_courant = self.id_ordre
                    self.journal.ecrire(t, "entree_envoyee", prix=close, qte=sens,
                                        id_ordre=self.id_courant,
                                        raison="market ×1 sur franchissement confirmé")
                    viser(self, self.nq, sens)
        self.rsi_prec = rsi

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
                     f"RSI={self.rsi.current.value:.1f} ATR={self.atr_entree:.1f} | @ {e} | "
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
        self.log(f"--- BILAN JUMEAU H3 RSI {PERIODE_RSI} + BRACKET NQ (signal 3 m, "
                 f"SL {STOP_MULT}×ATR{PERIODE_ATR}, TP {TP_R}R, sortie RSI {MOYENNE}, "
                 f"sans régime, flat 16:55 ET) ---")
        self.log(f"Entrées : {self.nb_entrees} | sorties : {self.nb_stop} SL, "
                 f"{self.nb_take} TP, {self.nb_signal} RSI {MOYENNE}, "
                 f"{self.nb_flat} flat forcé | garde-fou : {self.nb_garde_fou} | "
                 f"frais : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement : {equite / CAPITAL - 1:+.4%}")
        self.log(f"Journal de décisions : {self.journal.dossier}")
