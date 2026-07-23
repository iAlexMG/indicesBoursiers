# Cadre COMMUN des 3 jumeaux backtest des stratégies hybrides (volet C du chantier
# automatisation — specs : automatisation/docs/strategies-hybrides.md). Même rôle que
# nq_instrument.py : ce qui est PARTAGÉ par les jumeaux vit ici — heures de séance en ET,
# garde-fou journalier, cooldown, journal de décisions NDJSON. Le jumeau vit sous les
# MÊMES règles de séance que le futur code live, sinon la parité de la phase 4 (shadow)
# est fausse d'avance.
#
# Le journal est la SORTIE de parité : un fichier par jour ET par stratégie, une ligne
# par événement (format de strategies-hybrides.md §Journalisation). Correspondance avec
# le live — le jumeau SIMULE les ordres attachés dans la boucle 1 m :
#   - SL/TP touché : le live verra le FILL de l'ordre attaché ; le jumeau journalise la
#     détection en `sortie_envoyee` (raison SL/TP, prix = niveau simulé) puis le fill
#     LEAN en `fill` (prix d'exécution de la barre suivante).
#   - `bracket_pose` / `stop_modifie` / `annulation` : niveaux simulés, pas d'ordres.
#   - `kill` : sans objet en backtest (action manuelle du live).
# Sérialisation : json.dumps écrit TOUJOURS le point décimal — l'équivalent Python de
# l'InvariantCulture exigée côté C# (piège 6 du REPRISE, locale française du poste).
from datetime import timezone
from zoneinfo import ZoneInfo
import json
import os

NY = ZoneInfo("America/New_York")

# Défauts du cadre commun (volet A ; chiffres « à confirmer » par l'utilisateur) :
ENTREE_DEBUT = 9 * 60 + 30     # 09:30 ET — première entrée permise
ENTREE_FIN = 15 * 60 + 30      # 15:30 ET — plus rien de neuf après
FLAT_FORCE = 16 * 60 + 55      # 16:55 ET — tout annuler + liquider, quoi qu'il arrive
PERTES_MAX = 0                 # 0 = garde-fou DÉSACTIVÉ (refonte 07-20 : ne pas brider les
                               # signaux en phase de test) ; 2 pour tester ce mécanisme
COOLDOWN_MIN = 0               # recadrage 07-23 : 0 = ré-entrée dès la barre suivante (fréquence max)


def heure_ny(t_utc):
    """end_time LEAN (UTC naïf) -> (datetime ET, minutes depuis minuit ET).
    Conversion par zoneinfo, jamais d'offset en dur (l'heure d'été change l'écart)."""
    t = t_utc.replace(tzinfo=timezone.utc).astimezone(NY)
    return t, t.hour * 60 + t.minute


class Journal:
    """Journal de décisions NDJSON : un fichier par jour ET par stratégie — c'est CE
    fichier que la phase 4 comparera à celui du live. Réécrit à chaque run."""

    def __init__(self, strategie):
        self.strategie = strategie
        base = os.environ.get("JUMEAUX_JOURNAUX") or os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "..", "journaux")
        self.dossier = os.path.abspath(os.path.join(base, strategie))
        os.makedirs(self.dossier, exist_ok=True)
        self._fichier = None
        self._jour = None

    def ecrire(self, t_utc, evenement, prix=None, qte=None, id_ordre=None,
               raison="", **indicateurs):
        t_ny, _ = heure_ny(t_utc)
        if t_ny.date() != self._jour:
            self.fermer()
            self._jour = t_ny.date()
            chemin = os.path.join(self.dossier, f"{self._jour:%Y-%m-%d}.ndjson")
            self._fichier = open(chemin, "w", encoding="utf-8")
        ligne = {
            "ts": t_utc.replace(tzinfo=timezone.utc).isoformat(),
            "strategie": self.strategie,
            "symbole": "NQ",
            "evenement": evenement,
            "prix": None if prix is None else round(float(prix), 2),
            "qte": qte,
            "id_ordre": None if id_ordre is None else str(id_ordre),
            "raison": raison,
            "indicateurs": {k: round(float(v), 2)
                            for k, v in indicateurs.items() if v is not None},
        }
        self._fichier.write(json.dumps(ligne, ensure_ascii=False) + "\n")

    def fermer(self):
        if self._fichier is not None:
            self._fichier.close()
            self._fichier = None


class CadreSeance:
    """Garde-fou journalier (pertes pleines) + cooldown, ré-armés à 09:30 ET."""

    def __init__(self):
        self.garde_fou = False
        self.pertes_du_jour = 0
        self.temps_sortie = None
        self._jour_arme = None

    def maj_jour(self, t_ny, minutes):
        """À appeler à chaque barre : au premier passage de 09:30 ET du jour, remise à
        zéro (le garde-fou arrête « jusqu'au prochain 09:30 », pas jusqu'à minuit)."""
        if minutes >= ENTREE_DEBUT and self._jour_arme != t_ny.date():
            self._jour_arme = t_ny.date()
            self.pertes_du_jour = 0
            self.garde_fou = False

    def cooldown_ok(self, t):
        return (self.temps_sortie is None
                or (t - self.temps_sortie).total_seconds() >= COOLDOWN_MIN * 60)

    def sortie(self, t, perte_pleine):
        """Enregistre une sortie ; renvoie True si le garde-fou VIENT de se déclencher."""
        self.temps_sortie = t
        if perte_pleine:
            self.pertes_du_jour += 1
            if PERTES_MAX > 0 and self.pertes_du_jour >= PERTES_MAX and not self.garde_fou:
                self.garde_fou = True
                return True
        return False
