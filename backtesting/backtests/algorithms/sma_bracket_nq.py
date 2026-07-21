# H1 — Croisement SMA 9/21 (1 m) + bracket — JUMEAU BACKTEST de l'hybride H1 (refonte
# 2026-07-20 ; specs : automatisation/docs/strategies-hybrides.md). Remplace orb_nq.py.
# Déclencheur COMMUN aux 3 hybrides (croisement SMA 1 m) ; H1 = le bracket, résolu par SL/TP.
# Jumeau du code live : automatisation/hybrides/SmaBracketHybride.cs (mêmes formules).
#   - Signal : croisement SMA 9/21 sur closes 1 m.
#   - Entrée : croisement -> market ×1 + bracket SL 1,5×ATR14(1 m) / TP 1R (simulé, boucle 1 m).
#   - Sorties : TP, SL, ou flat forcé 16:55 ET. Ignore les croisements suivants tant qu'en
#     position (le bracket referme seul — c'est ce que H1 prouve).
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL
from cadre_hybride import CadreSeance, Journal, heure_ny, ENTREE_DEBUT, ENTREE_FIN, FLAT_FORCE, PERTES_MAX

SMA_RAPIDE = 9
SMA_LENTE = 21
PERIODE_ATR = 14
STOP_MULT = 1.5           # SL = entrée ∓ 1,5 × ATR (= R)
TP_R = 1.0               # TP = entrée ± 1 R
CONTRATS = 1


class SmaBracketNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.rapide = SimpleMovingAverage(SMA_RAPIDE)
        self.lente = SimpleMovingAverage(SMA_LENTE)
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.diff_prec = None

        self.cadre = CadreSeance()
        self.journal = Journal("sma_bracket_nq")
        self.prix_entree = None
        self.stop_prix = None
        self.take_prix = None
        self.atr_entree = None
        self.sortie_en_cours = False
        self.raison = ""
        self.id_ordre = 0
        self.id_courant = None
        self.nb_entrees = self.nb_stop = self.nb_take = self.nb_flat = 0
        self.frais_totaux = 0.0

    def _sortir(self, t, code, niveau, raison, perte_pleine, evenement="sortie_envoyee"):
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
        self.cadre.maj_jour(t_ny, m)

        self.atr.update(TradeBar(t, self.nq, float(bar["open"]), haut, bas, close, 0.0))
        self.rapide.update(t, close)
        self.lente.update(t, close)
        croisement = 0
        if self.rapide.is_ready and self.lente.is_ready:
            diff = self.rapide.current.value - self.lente.current.value
            if self.diff_prec is not None:
                if self.diff_prec <= 0 and diff > 0:
                    croisement = 1
                elif self.diff_prec >= 0 and diff < 0:
                    croisement = -1
            self.diff_prec = diff

        # 1) EN POSITION : bracket simulé (SL prioritaire), puis flat forcé.
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
            return    # en position, H1 ignore les croisements

        # 2) ENTRÉE sur croisement.
        pos = self.portfolio[self.nq]
        if (croisement == 0 or pos.invested or self.sortie_en_cours or not self.atr.is_ready
                or self.cadre.garde_fou or not self.cadre.cooldown_ok(t)
                or not (ENTREE_DEBUT < m <= ENTREE_FIN)):
            return
        sens = CONTRATS if croisement > 0 else -CONTRATS
        sens_txt = "haussier -> long" if croisement > 0 else "baissier -> short"
        self.atr_entree = float(self.atr.current.value)
        self.id_ordre += 1
        self.id_courant = self.id_ordre
        indic = dict(sma_rapide=self.rapide.current.value, sma_lente=self.lente.current.value,
                     atr=self.atr_entree)
        self.journal.ecrire(t, "signal", prix=close, raison=f"croisement {sens_txt}", **indic)
        self.journal.ecrire(t, "entree_envoyee", prix=close, qte=sens, id_ordre=self.id_courant,
                            raison="market ×1 sur croisement")
        viser(self, self.nq, sens)

    def on_order_event(self, event: OrderEvent):
        if event.status != OrderStatus.FILLED:
            return
        self.frais_totaux += float(event.order_fee.value.amount)
        t = event.utc_time
        pos = self.portfolio[self.nq]
        if pos.invested:
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
            self.journal.ecrire(t, "bracket_pose", prix=e, id_ordre=f"{self.id_courant}-bracket",
                                raison=f"SL {STOP_MULT}×ATR / TP {TP_R}R (simulé, boucle 1 m)",
                                stop=self.stop_prix, take=self.take_prix, atr=self.atr_entree)
        else:
            s = float(event.fill_price)
            self.journal.ecrire(t, "fill", prix=s, qte=int(event.fill_quantity),
                                id_ordre=self.id_courant, raison=f"fill de sortie [{self.raison}]")
            self.prix_entree = self.stop_prix = self.take_prix = None
            self.sortie_en_cours = False

    def on_end_of_algorithm(self):
        self.journal.fermer()
        equite = float(self.portfolio.total_portfolio_value)
        self.log(f"--- BILAN JUMEAU H1 SMA {SMA_RAPIDE}/{SMA_LENTE} + BRACKET NQ (1 m, "
                 f"SL {STOP_MULT}×ATR{PERIODE_ATR}, TP {TP_R}R, flat 16:55 ET) ---")
        self.log(f"Entrées : {self.nb_entrees} | sorties : {self.nb_stop} SL, "
                 f"{self.nb_take} TP, {self.nb_flat} flat forcé | frais : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement : {equite / CAPITAL - 1:+.4%}")
