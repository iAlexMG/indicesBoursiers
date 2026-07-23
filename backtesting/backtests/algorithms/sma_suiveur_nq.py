# H2 — Croisement SMA 2/6 (1 m) + stop suiveur — JUMEAU BACKTEST de l'hybride H2 (refonte
# 2026-07-20 ; specs : automatisation/docs/strategies-hybrides.md). Déclencheur COMMUN aux 3
# hybrides (croisement SMA 1 m) ; H2 = la MODIFICATION (stop suiveur remonté plusieurs fois).
# Jumeau du code live : automatisation/hybrides/SmaSuiveurHybride.cs (mêmes formules).
#   - Signal : croisement SMA 2/6 sur closes 1 m. Croisement -> market ×1 + SL 2×ATR14(1 m),
#     pas de TP.
#   - Suiveur : à chaque barre 1 m, stop = extrême favorable ∓ 2×ATR, ne recule jamais.
#   - Sorties : stop, croisement inverse (annulation + market), flat forcé 16:55 ET.
from AlgorithmImports import *
import os, sys
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from nq_instrument import setup_nq, viser, CAPITAL
from cadre_hybride import CadreSeance, Journal, heure_ny, ENTREE_DEBUT, ENTREE_FIN, FLAT_FORCE, PERTES_MAX

SMA_RAPIDE = 2
SMA_LENTE = 6
PERIODE_ATR = 7
STOP_MULT = 2.0           # stop / suiveur = extrême favorable ∓ 2 × ATR
CONTRATS = 1


class SmaSuiveurNq(QCAlgorithm):

    def initialize(self):
        self.nq = setup_nq(self)
        self.rapide = SimpleMovingAverage(SMA_RAPIDE)
        self.lente = SimpleMovingAverage(SMA_LENTE)
        self.atr = AverageTrueRange(PERIODE_ATR, MovingAverageType.WILDERS)
        self.diff_prec = None

        self.cadre = CadreSeance()
        self.journal = Journal("sma_suiveur_nq")
        self.prix_entree = None
        self.stop_prix = None
        self.extreme = None
        self.atr_entree = None
        self.sortie_en_cours = False
        self.raison = ""
        self.id_ordre = 0
        self.id_courant = None
        self.nb_entrees = self.nb_stop = self.nb_signal = self.nb_flat = 0
        self.nb_modifs = self.nb_garde_fou = 0
        self.frais_totaux = 0.0

    def _sortir(self, t, code, niveau, raison, perte_pleine, evenement="sortie_envoyee"):
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
        atr = float(self.atr.current.value) if self.atr.is_ready else None
        indic = dict(sma_rapide=self.rapide.current.value, sma_lente=self.lente.current.value, atr=atr)

        # 1) EN POSITION : extrême favorable, stop simulé, croisement inverse, flat.
        pos = self.portfolio[self.nq]
        if pos.invested and not self.sortie_en_cours and self.stop_prix is not None:
            self.extreme = max(self.extreme, haut) if pos.is_long else min(self.extreme, bas)
            if (pos.is_long and bas <= self.stop_prix) or (pos.is_short and haut >= self.stop_prix):
                perte = (self.stop_prix < self.prix_entree if pos.is_long
                         else self.stop_prix > self.prix_entree)
                self._sortir(t, "STOP", self.stop_prix,
                             "stop touché" + (" (perte pleine)" if perte else " (gain/breakeven)"),
                             perte_pleine=perte)
                return
            if m >= FLAT_FORCE:
                self.journal.ecrire(t, "annulation", id_ordre=f"{self.id_courant}-sl",
                                    raison="flat forcé : stop annulé")
                self._sortir(t, "FLAT", close, "flat forcé 16:55 ET", perte_pleine=False,
                             evenement="flat_force")
                return
            if (pos.is_long and croisement < 0) or (pos.is_short and croisement > 0):
                self.journal.ecrire(t, "signal", prix=close, raison="croisement inverse -> sortie", **indic)
                self.journal.ecrire(t, "annulation", id_ordre=f"{self.id_courant}-sl",
                                    raison="sortie signal : stop annulé")
                self._sortir(t, "SIGNAL", close, "croisement inverse", perte_pleine=False)
                return
            if atr is not None:                      # stop suiveur, ne recule jamais
                candidat = (self.extreme - STOP_MULT * atr if pos.is_long
                            else self.extreme + STOP_MULT * atr)
                if (pos.is_long and candidat > self.stop_prix) or (pos.is_short and candidat < self.stop_prix):
                    self.stop_prix = candidat
                    self.nb_modifs += 1
                    self.journal.ecrire(t, "stop_modifie", prix=self.stop_prix,
                                        id_ordre=f"{self.id_courant}-sl", raison="suiveur",
                                        extreme=self.extreme, atr=atr)
            return

        # 2) ENTRÉE sur croisement.
        pos = self.portfolio[self.nq]
        if (croisement == 0 or pos.invested or self.sortie_en_cours or atr is None
                or self.cadre.garde_fou or not self.cadre.cooldown_ok(t)
                or not (ENTREE_DEBUT < m <= ENTREE_FIN)):
            return
        sens = CONTRATS if croisement > 0 else -CONTRATS
        sens_txt = "haussier -> long" if croisement > 0 else "baissier -> short"
        self.atr_entree = atr
        self.id_ordre += 1
        self.id_courant = self.id_ordre
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
            self.extreme = e
            self.nb_entrees += 1
            self.stop_prix = (e - STOP_MULT * self.atr_entree if pos.is_long
                              else e + STOP_MULT * self.atr_entree)
            self.journal.ecrire(t, "fill", prix=e, qte=int(event.fill_quantity),
                                id_ordre=self.id_courant, raison="fill d'entrée")
            self.journal.ecrire(t, "bracket_pose", prix=e, id_ordre=f"{self.id_courant}-sl",
                                raison=f"SL seul {STOP_MULT}×ATR, pas de TP (simulé, boucle 1 m)",
                                stop=self.stop_prix, atr=self.atr_entree)
        else:
            s = float(event.fill_price)
            self.journal.ecrire(t, "fill", prix=s, qte=int(event.fill_quantity),
                                id_ordre=self.id_courant, raison=f"fill de sortie [{self.raison}]")
            self.prix_entree = self.stop_prix = self.extreme = None
            self.sortie_en_cours = False

    def on_end_of_algorithm(self):
        self.journal.fermer()
        equite = float(self.portfolio.total_portfolio_value)
        self.log(f"--- BILAN JUMEAU H2 SMA {SMA_RAPIDE}/{SMA_LENTE} + SUIVEUR NQ (1 m, "
                 f"stop {STOP_MULT}×ATR{PERIODE_ATR}, flat 16:55 ET) ---")
        self.log(f"Entrées : {self.nb_entrees} | sorties : {self.nb_stop} stop, "
                 f"{self.nb_signal} croisement inverse, {self.nb_flat} flat | "
                 f"stop modifié {self.nb_modifs} fois | garde-fou : {self.nb_garde_fou} | "
                 f"frais : {self.frais_totaux:.2f} $")
        self.log(f"Équité finale : {equite:.2f} $ | rendement : {equite / CAPITAL - 1:+.4%}")
