"""Configuration du logging du tableau de bord.

Porté de `crypto/affichage/gui/logsetup.py`, avec UNE addition rendue nécessaire par le
lanceur « sans console » (voir plus bas). Log de DÉBOGAGE uniquement : un seul fichier
`logs/indices.log` ÉCRASÉ à chaque lancement (mode 'w') — inutile de garder l'historique
des sessions.

On ne journalise JAMAIS les données de marché (contenu des carnets/trades) : uniquement les
ÉVÈNEMENTS — connexions, contrats résolus, accès qui tombe ou revient, rétention.

Pourquoi `logging` plutôt que `print()`, ici précisément :
  - **Les accès écrivent depuis des THREADS différents.** `print()` écrit le texte puis le
    saut de ligne en DEUX opérations : deux threads qui parlent en même temps produisent des
    lignes collées (mesuré : « …nouvel essai dans 1 s[quantower] [NQ] pont indisponible… »).
    `logging` sérialise chaque enregistrement derrière un verrou.
  - **Sous `pythonw.exe`, `sys.stdout` et `sys.stderr` valent `None`** : `print()` ne lève
    pas, il est SILENCIEUSEMENT jeté, tracebacks compris. Un `FileHandler` écrit dans le
    fichier quoi qu'il arrive — c'est ce qui rend le lanceur « sans console » utilisable.
"""
from __future__ import annotations

import logging
import os
import sys


def setup_logging(level: int = logging.INFO) -> str:
    os.makedirs("logs", exist_ok=True)
    path = os.path.join("logs", "indices.log")
    root = logging.getLogger()
    root.setLevel(level)
    if any(getattr(h, "_indices", False) for h in root.handlers):
        return path                                   # déjà configuré (appel double)
    fmt = logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s",
                            datefmt="%Y-%m-%d %H:%M:%S")
    fh = logging.FileHandler(path, mode="w", encoding="utf-8")   # ÉCRASE à chaque lancement
    fh.setFormatter(fmt); fh._indices = True          # type: ignore[attr-defined]
    root.addHandler(fh)

    # Console : SEULEMENT si elle existe. `StreamHandler()` écrit sur `sys.stderr`, que
    # `pythonw.exe` met à None — l'attacher quand même ferait échouer chaque émission.
    # Le frère crypto n'a pas ce garde-fou ; il ne s'en aperçoit pas parce que son lanceur
    # silencieux ne compte que sur le fichier.
    if sys.stderr is not None:
        ch = logging.StreamHandler()
        ch.setFormatter(fmt); ch._indices = True      # type: ignore[attr-defined]
        root.addHandler(ch)

    # ib_insync raconte sa vie à chaque tick en INFO : on ne garde que ce qui cloche.
    # Ses erreurs de connexion (« API connection failed ») sont en ERROR -> conservées.
    logging.getLogger("ib_insync").setLevel(logging.WARNING)
    return path
