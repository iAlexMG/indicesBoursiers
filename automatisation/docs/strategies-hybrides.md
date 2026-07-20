# Stratégies hybrides — specs exécutables (volet A du chantier automatisation)

> Rédigé le 2026-07-19, resserré le même soir. Pivot : on n'automatise **pas** les 8
> stratégies du pilier backtesting (artefacts de backtest : signal sur clôture, aucun ordre
> réellement placé). Hybride = **entrée simple × vraie gestion d'ordres** (SL/TP en ordres
> attachés, cadre de séance, garde-fous prop firm).
>
> Décisions utilisateur du 2026-07-19 : 3 stratégies, **une par mécanique** (cassure /
> tendance / retour à la moyenne) ; contrat **NQ mini ×1** ; stop en **k×ATR14**, cible en
> **multiple du risque (R)** ; garde-fou journalier en **nombre de pertes**.

## L'objectif : prouver que ça fonctionne — PAS que c'est rentable

Cadrage utilisateur (2026-07-19) : l'historique disponible est trop court pour des backtests
crédibles, donc **l'objectif de tout le projet est la preuve de concept et de
fonctionnalités**, pas la découverte d'une stratégie rentable. Conséquences directes sur les
specs :

- **Très simples et basiques** : pas de filtre de régime, pas de sizing par volatilité, pas
  de multi-timeframe savant. Une condition d'entrée, un bracket, des garde-fous. (Les filtres
  de régime de la v1 de cette spec — SMA 48 en 15 m, SMA 50 en 3 m — sont **retirés** : des
  features de performance, et les plus gros consommateurs d'historique.)
- **Compatible faible historique** : chaque indicateur se **seed via l'historique de la
  plateforme** (`GetHistory`) au démarrage — le besoin réel se compte en *heures* de barres
  (le plus gourmand : 21 barres de 5 m ≈ 1 h 45), très loin sous les ~2 semaines de ticks ou
  les mois de barres 1 m disponibles.
- **La rentabilité est un non-objectif assumé.** Un jour perdant qui déclenche proprement le
  garde-fou est un **succès de test**, pas un échec. C'est le même récit que le reste du
  portfolio : la rigueur se démontre, la performance non.

### Critère de succès du pilier (ce qui doit être PROUVÉ, journal à l'appui)

Chaque mécanisme observé **au moins une fois** dans le journal NDJSON (+ capture pour le
site) : bracket posé au fill · stop suiveur modifié (plusieurs fois dans un même trade) ·
bracket annulé sur sortie signal · TP touché · SL touché · flat forcé de 16:55 exécuté ·
garde-fou journalier déclenché · kill switch exécuté · redémarrage propre le lendemain.

---

## Cadre commun (les trois stratégies le partagent intégralement)

| Élément | Spec | Statut |
|---|---|---|
| Instrument | **NQ front (mini), 1 contrat**, long et short | décidé |
| Position | **Une à la fois**, pas de pyramidage, pas de renversement direct (flat → cooldown → entrée) | décidé |
| Entrées permises | **09:30 → 15:30 ET** seulement (RTH ; rien de neuf après 15:30) | défaut, à confirmer |
| Flat forcé | **16:55 ET** : annuler tous les ordres + liquider, quoi qu'il arrive | défaut, à confirmer |
| Garde-fou journalier | **2 sorties en perte pleine (stop touché) → stratégie arrêtée jusqu'au prochain 09:30** | défaut (« 2-3 » évoqué), à confirmer |
| Cooldown | **15 min** après toute sortie — juste assez pour garder un journal lisible (les 45-60 min du banc étaient de l'anti-frais, un souci de performance) | défaut |
| Kill switch | Paramètre/action manuelle : **tout annuler + liquider + ne plus entrer**. L'arrêt de la stratégie fait pareil. | décidé |
| Flux gelé / déconnexion | Détection (leçon piège 17 du REPRISE) → **aucune nouvelle entrée** ; le sort des stops déjà posés dépend d'où ils vivent (→ question centrale du volet B) | à trancher au volet B |
| Warm-up | Indicateurs **seedés par `GetHistory`** au démarrage — jamais « attendre que ça chauffe » en séance | décidé |
| Fuseau | Toutes les heures en **ET** explicite (jamais l'heure locale implicite du poste) | décidé |

⚠️ **Chiffres Apex** : le garde-fou en nombre de pertes évite d'avoir à connaître le trailing
drawdown. Si un plafond en $ s'ajoute plus tard, les chiffres du compte Legacy 250k viennent
de l'utilisateur, jamais de la doc publique d'Apex.

Ordre de grandeur du risque (échelle, pas règle) : NQ = 20 $/point ; un stop de 1,5×ATR14
par vol courante = quelques dizaines de points, donc quelques centaines à ~1 500 $ par perte
pleine. D'où le garde-fou à 2.

---

## H1 — Cassure de plage d'ouverture (ORB) · mécanique : cassure · prouve : le bracket

Née intraday, compatible séance par construction. **Au plus une entrée par jour** (la
première cassure confirmée).

- **Plage** : plus-haut/plus-bas de **09:30 → 10:00 ET** (barres 1 m). Variante 15 min
  possible — à trancher à l'usage.
- **Fenêtre d'entrée** : 10:00 → 12:00 ET. Pas de cassure avant midi = journée sans trade.
- **Entrée** : première **clôture 1 m** au-delà d'une borne (haut → long, bas → short) →
  **ordre market** + **bracket SL/TP attaché**.
  - Variante à évaluer au volet B : deux **ordres stop d'entrée** posés aux bornes en OCO.
    La spec de base reste la clôture confirmée : plus simple, insensible aux mèches.
- **SL** : 1,5 × ATR14 (barres 5 m, seedé, valeur à l'instant d'entrée). **TP : 2R.**
- **Sorties** : TP, SL, ou flat forcé 16:55 ET. Pas de stop suiveur ici.
- **Après sortie** : terminé pour la journée, gagnée ou perdue.

## H2 — Croisement SMA + stop suiveur · mécanique : tendance · prouve : la MODIFICATION

L'esprit de `sma_croisement_nq.py`, réduit à l'os : le croisement seul, **sans filtre de
régime**. **C'est la stratégie au stop suiveur.**

- **Signal** : aux bornes de **5 m** (plus d'événements qu'en 15 m — on veut exercer la
  chaîne, pas économiser des frais). SMA **9/21** sur closes 5 m, seedées.
- **Entrée** : croisement (rapide traverse la lente) → **market ×1** dans le sens du
  croisement + **SL attaché** initial à **2 × ATR14 (5 m)**. **Pas de TP** : la sortie
  naturelle est le stop suiveur ou le croisement inverse.
- **Stop suiveur** : à chaque clôture 5 m, remonter (long) le SL à
  `max(SL courant, extrême favorable depuis l'entrée − 2×ATR14)` — **modification de l'ordre
  stop existant**. Le stop ne recule jamais. C'est le mécanisme que H2 doit prouver, plusieurs
  fois par trade.
- **Sorties** : stop (initial ou suiveur), **croisement inverse** (→ market de sortie +
  annulation du stop), ou flat forcé 16:55 ET. Cooldown 15 min, puis le prochain croisement
  peut rentrer — dans un sens ou l'autre.

## H3 — RSI retour à la moyenne · mécanique : contre-tendance · prouve : l'ANNULATION

L'esprit de `rsi_retour_moyenne_nq.py`, **sans filtre de régime** : le fade se prend des deux
bords, le bracket encadre tout.

- **Signal** : aux bornes de **3 m**. RSI **9** (Wilder, seedé) : franchissement **sous 30**
  → long ; franchissement **au-dessus de 70** → short.
- **Entrée** : market ×1 + **bracket SL/TP** complet.
- **SL** : 1,5 × ATR14 (3 m). **TP : 1R** (cible courte assumée — retour à la moyenne).
  Ratio à confirmer (1R vs 1,5R).
- **Sortie anticipée** : RSI revenu à ~50 → market de sortie + **annulation du bracket** —
  le mécanisme que H3 doit prouver.
- **Sorties** : TP, SL, RSI 50, ou flat forcé 16:55 ET. Cooldown 15 min.

---

## Journalisation (les trois, format commun)

Objectif double : **preuve fonctionnelle** (le critère de succès ci-dessus se lit dans ce
journal) et **parité** future avec un rejeu (l'esprit du shadow mode, phase 4). Un fichier
**NDJSON par jour et par stratégie**, une ligne par événement :

- `ts` (UTC, ISO), `strategie`, `symbole`, `evenement`
  (`signal|entree_envoyee|fill|bracket_pose|stop_modifie|sortie_envoyee|annulation|garde_fou|flat_force|kill`),
- `prix`, `qte`, `id_ordre`, `raison` (texte court), et les **valeurs d'indicateurs au moment
  de la décision** (SMA/RSI/ATR, bornes ORB) — c'est ce qui rend la décision rejouable.
- ⚠️ Sérialisation en **InvariantCulture** (locale française du poste, piège 6 du REPRISE) ;
  écriture hors du thread de marché (patron du pont NqFeed : file bornée + thread d'écriture).

## Ce que ces specs exigent de la plateforme — l'entrée du volet B

Le volet B (étude Simulator) doit vérifier chacun de ces mécanismes **dans les deux modes**
(compte papier Simulator ET compte réel Rithmic/Apex), ou le marquer manquant avec une
alternative :

1. **Market + SL/TP attachés** (bracket) — pose atomique ou séquentielle ? (H1, H3)
2. **Modification du prix d'un stop** existant, à cadence 5 m (H2 suiveur).
3. **Annulation d'un bracket** entier lors d'une sortie sur signal (H2 inverse, H3 RSI 50).
4. **Tout annuler + liquider** en un geste fiable (flat 16:55, garde-fou, kill switch).
5. **Événements de fill** exploitables (prix, heure, qté). Fills partiels : non-sujet à
   1 contrat, à noter quand même.
6. **Où vit le stop : serveur Rithmic ou plateforme ?** Fidélité sim vs réel ET sécurité
   (que devient le stop si Quantower plante ?). — la question la plus lourde.
7. Ordres **stop d'entrée** + OCO d'entrée (variante H1 seulement — optionnel).
8. Choix du **compte/connexion en paramètre** (le même code pointe papier ou réel).
9. **Seed des indicateurs par `GetHistory`** (quelques heures de barres suffisent) + calcul
   aux bornes de 3/5 m — les deux patrons existent déjà dans les indicateurs Phase 3.

## Jumeaux backtest — le volet C — ✅ FAIT (2026-07-19 soir)

**Livré** : `backtesting/backtests/algorithms/{orb,sma_suiveur,rsi_bracket}_nq.py` +
`cadre_hybride.py` (cadre commun : séance ET, garde-fou, cooldown, journal NDJSON — même
rôle que `nq_instrument.py`). Les 3 backtests LEAN menés sur la fenêtre du banc
(06-01 → 07-10), journaux dans `backtesting/backtests/journaux/<strategie>/` (hors git,
régénérés à chaque run), invariants vérifiés (fenêtres d'entrée ET, suiveur jamais en
recul, aucune entrée sous garde-fou, flat forcé ≥ 16:55) :

- **H1 ORB** : 28 entrées (au plus une/jour ✓) — 22 SL, 5 TP, 1 flat forcé.
- **H2 SMA+suiveur** : 75 entrées — stop **modifié 617 fois** (~8/trade, LE mécanisme à
  prouver), 58 stops, 13 croisements inverses, 4 flats, garde-fou 11×.
- **H3 RSI+bracket** : 103 entrées — 45 SL, 41 TP, 17 **annulations sur RSI 50**,
  garde-fou 17×.

Rappel du cadrage : référence de décisions, PAS un verdict de performance.
🪤 **Trouvaille du jumeau — clôture avancée** : le 3 juillet (CME ferme à 13:00 ET,
veille du 4 juillet), la barre de 16:55 n'existe pas → H2 a porté sa position **tout le
week-end** et l'a liquidée à la réouverture (dimanche 18:01 ET). L'angle mort est dans
la SPEC (le flat 16:55 suppose une séance normale) → décision restante ci-dessous.

### L'énoncé d'origine (conservé pour référence)

Les 3 hybrides **doivent être backtestées et donc ajoutées au pilier Backtesting** (décision
utilisateur). Nécessité structurelle : la phase 4 (shadow mode) se définit comme « prouver
qu'en réel ils décident comme au backtest » — sans jumeau, rien à comparer.

- **Rangement** : 3 nouveaux fichiers **à côté des 8** dans
  `backtesting/backtests/algorithms/` — `orb_nq.py`, `sma_suiveur_nq.py`,
  `rsi_bracket_nq.py` — même patron et même `nq_instrument` que le banc. **Les 8 ne bougent
  pas.**
- **Modélisation** : SL/TP/suiveur simulés **dans la boucle** sur barres 1 m (extrêmes
  intra-barre), exactement le patron de `risque_stops_nq.py`. Le jumeau reproduit les
  **décisions** (quand entrer, où est le stop, quand il monte) ; la mécanique d'ordres
  réelle, elle, se prouve en live — c'est la répartition des rôles.
- **Cadre de séance modélisé aussi** : entrées 09:30-15:30 ET, flat 16:55 ET, garde-fou
  2 pertes — le jumeau doit vivre sous les mêmes règles, sinon la parité phase 4 est fausse
  d'avance.
- **Sortie** : chaque jumeau écrit son **journal de décisions** au même format NDJSON que le
  live — c'est ce fichier contre ce fichier que la phase 4 comparera.
- **Fenêtre** : celle du banc (2026-06-01 → 07-10) par cohérence. ⚠️ Rappel du cadrage :
  ~28 séances = **référence de décisions et validation de logique, PAS un verdict de
  performance** — l'historique est trop court pour être crédible, et c'est assumé.
- **Séquence décidée** : volet B (étude Simulator) **d'abord** — si un mécanisme s'avère
  impossible, la spec change et le jumeau aurait été à refaire. Puis volet C (jumeaux), puis
  le code live.
- Fiches site (hub Backtesting) : **plus tard**, et poser la question avant d'écrire, comme
  d'habitude.

## Décisions restantes (utilisateur)

- Heure de flat forcé (défaut **16:55 ET**) et borne des entrées (défaut **15:30 ET**).
- **Clôtures avancées** (trouvaille du volet C) : les jours de séance écourtée (ex.
  3 juillet, CME ferme 13:00 ET), 16:55 n'existe pas — le live doit flatter AVANT la
  clôture réelle du jour (calendrier CME ou heure de flat paramétrable par jour), sinon
  la position porte jusqu'à la réouverture. À trancher avant le code live.
- Garde-fou : **2** ou 3 pertes pleines.
- H1 : plage 30 min (défaut) ou 15 min ; entrée sur clôture confirmée (défaut) ou stops OCO.
- H3 : TP **1R** (défaut) ou 1,5R.
