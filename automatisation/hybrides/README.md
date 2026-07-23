# Hybrides — le code LIVE des 3 stratégies + la sonde « Ordres Probe (SIM) »

> Écrit le 2026-07-20 (code live du chantier automatisation), **refondu le même jour**
> (déclencheur commun — voir la note en tête de
> [`../docs/strategies-hybrides.md`](../docs/strategies-hybrides.md)). Étude Simulator :
> [`../docs/etude-simulator.md`](../docs/etude-simulator.md) · jumeaux backtest :
> `../../backtesting/backtests/algorithms/sma_{bracket,suiveur,annule}_nq.py`.
> 🎯 Rappel du cadrage : **preuve de concept et de fonctionnalités, PAS la rentabilité.**

**Déclencheur COMMUN aux 3 : croisement SMA 9/21 sur closes 1 m.** Elles ne diffèrent que
par la gestion d'ordre. **UNE DLL (`Hybrides.dll`, net10.0), quatre stratégies** dans le
panneau Strategies :

| Classe | Nom dans Quantower | Sur croisement (commun) puis… | Prouve | Jumeau LEAN |
|---|---|---|---|---|
| `SmaBracketHybride` | Hybride H1 SMA Bracket (NQ) | bracket SL 1,5×ATR / TP 1R, attend SL/TP | le **bracket** | `sma_bracket_nq.py` |
| `SmaSuiveurHybride` | Hybride H2 SMA Suiveur (NQ) | SL 2×ATR + **stop suiveur** (modifié chaque barre) | la **modification** | `sma_suiveur_nq.py` |
| `SmaAnnuleHybride` | Hybride H3 SMA Annulation (NQ) | bracket SL 1,5×ATR / TP 2R, **annulé** au croisement inverse | l'**annulation** | `sma_annule_nq.py` |
| `OrdresProbe` | Ordres Probe (SIM) | (sonde, hors déclencheur commun) | le cycle de vie des ordres (étude §9) | — |

Cadre commun : cooldown 2 min, garde-fou **désactivé par défaut** (`Garde-fou = 0` en test),
ATR14 sur 1 m. **Séance : « Restreindre à la séance NY » est DÉCOCHÉ par défaut = mode 24 h**
(entrées quand le marché est ouvert, y compris le soir dès 18:00 ET, et PAS de flat de
séance) — pour tester/observer à toute heure. Le RE-COCHER pour la réalité prop firm
(entrées 09:30-15:30 ET, flat 16:55 ET), surtout en CONFIRMATION/AUTO sur compte réel. Le
même interrupteur existe sur l'indicateur visuel. ⚠️ Pour une comparaison de parité avec le
jumeau LEAN (phase 4), régler la séance PAREIL des deux côtés (le jumeau est bridé séance NY).
Fréquence obtenue au banc (~28 séances, séance NY) : H1 442 entrées, H2 476 (stop modifié
3183×), H3 421 (111 annulations).

## Les TROIS modes d'exécution (paramètre « Mode d'exécution »)

Contexte (2026-07-20) : l'essai 7 jours du Simulator est **mort** (déjà consommé), et
**Apex interdit les bots** — mais permet le trading où c'est l'HUMAIN qui initie chaque
transaction (dixit l'utilisateur, source d'autorité sur ses règles).

1. **SHADOW (défaut)** — la phase 4 : la stratégie décide et SIMULE le cycle de vie des
   ordres **au tick** (fill au trade suivant, bracket à chaque trade, suiveur,
   annulations) — le moteur des jumeaux LEAN porté au live. AUCUN appel à l'API d'ordres,
   aucun compte requis. Journal ids `shadow-N`.
2. **CONFIRMATION** — le semi-automatisé, l'humain dans la boucle : **l'humain initie chaque
   POSITION.** Les **entrées** et les **sorties sur signal** (croisement inverse) sont
   **PROPOSÉES par un pop-up Alert** (`Utils.Alert.ActionOnConfirm`, mesuré ✅) — rien ne
   s'ouvre/se ferme discrétionnairement sans son clic. Confirmer = accepter ; ignorer = refus
   (expiration paramétrable, défaut 120 s ; journal ids `prop-N`). En revanche, la **gestion
   protectrice** d'une position déjà confirmée s'applique **AUTOMATIQUEMENT** : **stop suiveur**
   (resserrement — sinon un pop-up par barre, impraticable pour H2) et **flat de fin de séance**
   (obligatoire côté prop firm) ; ça ne fait que réduire le risque. Le SL/TP est exécuté par
   l'ordre attaché. Le bouton Stop (clic humain) déclenche le Flatten du kill switch. Sur
   compte réel, « Autoriser un compte réel » = DEUXIÈME consentement. (Lecture des règles Apex :
   l'humain initie chaque position, la protection est automatique — à confirmer par l'utilisateur.)
3. **AUTO** — ordres directs sans confirmation : Trading Simulator (s'il est acheté un
   jour — pack Advanced Features / All-in-One, garantie 10 j) ou phase 5. La sonde
   logicielle « Ordres Probe (SIM) » reste prête pour ce cas.

La checklist mécanique peut aussi se dérouler À LA MAIN
([`../docs/sonde-manuelle-apex.md`](../docs/sonde-manuelle-apex.md)).

## Sécurité — les garde-fous d'abord

- **Garde anti-compte-réel** (hors shadow) : les 3 stratégies REFUSENT de démarrer si la
  connexion du compte n'est pas `TradingSimulator`, sauf si « Autoriser un compte réel
  (phase 5) » est coché. **La sonde, elle, n'a AUCUN paramètre d'évasion** (codé en dur).
- **Kill switch** = arrêter la stratégie (bouton Stop) : tout annuler + liquider
  (`Flatten`), journalisé `kill`.
- **Flat forcé sur horloge murale** (timer 10 s), PAS sur les barres : un marché muet à
  16:55 n'empêche pas le flat. 🪤 **Clôtures avancées** (trouvaille du volet C) : les jours
  de séance écourtée (ex. 3 juillet, CME 13:00 ET), AVANCER le paramètre « Flat forcé à ».
- Garde-fou journalier (2 pertes pleines → arrêt jusqu'à 09:30 ET), cooldown 15 min,
  fenêtre d'entrées 09:30-15:30 ET — tous paramétrables, mêmes défauts que les specs.
- Flux gelé (piège 17) : > 5 min sans trade en séance = avertissement au log ; pas de
  barre → pas de signal → pas d'entrée, par construction.

## Architecture (mesurée, jamais supposée — dump réflexif du 2026-07-20)

- **Même code sim/réel** : le compte est un `[InputParameter]` (verdict n°1 de l'étude).
- **Ordres** : entrée = `PlaceOrder` market + `SlTpHolder.CreateSL/CreateTP` en **Offset
  (ticks depuis le fill)** ; suiveur H2 = `ModifyOrder(TriggerPrice)` sur l'ordre stop lié
  (`PositionId`) ; sortie signal = `Cancel()` des brackets + `Position.Close()` ; flat/kill
  = `AdvancedTradingOperations.Flatten(symbole, compte)` — un seul geste.
- **Barres** : seed d'indicateurs par `GetHistory` (48 h de barres 1 m, jamais « attendre
  que ça chauffe »), puis barres 1 m VIVANTES reconstruites depuis `Symbol.NewLast` (le
  flux trades prouvé par le pont NqFeed). Agrégation 3/5 m et formules SMA/RSI/ATR
  (Wilder) IDENTIQUES aux jumeaux — voir la parité ci-dessous.
- **Journal NDJSON** (`H:\IndicesBoursiers\automatisation\journaux\<slug>\AAAA-MM-JJ.ndjson`,
  parametrable) : même format que les jumeaux, file bornée + thread d'écriture (patron
  NqFeed), `InvariantCulture`. En APPEND (un redémarrage complète le fichier du jour) ;
  événements `demarrage`/`arret` en plus du vocabulaire des specs (le critère « redémarrage
  propre le lendemain » doit se lire dans le journal).
- Correspondance live ↔ jumeau : un SL/TP touché produit ici un simple `fill` de l'ordre
  attaché (le jumeau journalise `sortie_envoyee` à la détection — mapping documenté dans
  `cadre_hybride.py`).

## Parité indicateurs C# ↔ LEAN — MESURÉE

`parite/` rejoue le CSV 1 m canonique du banc dans les classes de `Indicateurs.cs` et
compare aux valeurs que LEAN a écrites dans les journaux des jumeaux :

```powershell
cd parite; dotnet run -c Release            # -> parite_csharp.csv (fenêtre du banc)
& $HOME\miniconda3\envs\backtesting\python.exe compare_parite.py
```

Résultat (fenêtre 06-01 → 07-10) : refonte 1 m du 2026-07-20 → **9 034 comparaisons, 0 écart**
(SMA 9/21 + ATR14, tout sur 1 m). Parité indicateurs parfaite.

## Parité PHASE 4 — décisions live (shadow) ↔ jumeau backtest

`parite/parite_shadow.py` compare les `signal` (minute + sens) du **journal SHADOW live**
(`H:\IndicesBoursiers\automatisation\journaux\<slug>\<date>.ndjson`) au **jumeau backtest du
même jour** (`backtesting/backtests/journaux/<slug>/<date>.ndjson`), dans les fenêtres où le
shadow tournait. C'est la mesure « en réel, décide-t-il comme au backtest ? ».

**Le plus simple — `parite/run_parite.py` (« un bouton »)** enchaîne tout pour une date :
régénère le CSV, règle la fenêtre de `nq_instrument.py` sur `[cible-amorçage .. cible+2]`,
rejoue les 3 jumeaux LEAN, **restaure la fenêtre du banc (06-01→07-10) même en cas de plantage**,
puis compare. On le lance **le LENDEMAIN** de la séance shadow :
```powershell
python run_parite.py --date 2026-07-23        # séance shadow du 07-23, lancé le 07-24
```
🪤 **LEAN REFUSE de backtester le JOUR COURANT** (`set_end_date` rabattu à hier) → la date
cible doit être STRICTEMENT dans le passé (le script le vérifie et refuse sinon).

Outil bas niveau, si besoin :
```powershell
python parite_shadow.py --lister <journal.ndjson>           # dump des signaux d'un journal
python parite_shadow.py --slug sma_bracket_nq --date 2026-07-23
```
⚠️ **Pour un artefact qui a du sens** : shadow **PUR** (sans confirmation) sur une **séance NY
complète**, « **Restreindre à la séance NY** RE-COCHÉ » (le jumeau est bridé séance NY — sinon
les signaux du soir sortent en « shadow seul », un artefact de config et pas une divergence),
extracteur `NQ-ES History Bars 1m` qui tourne en parallèle. ✅ Chaîne prouvée de bout en bout
le 2026-07-22 (extracteur → base 07-22 → CSV → 3 jumeaux → parité) ; `run_parite.py` validé
sur 07-21.

## Déployer & lancer

```powershell
powershell -ExecutionPolicy Bypass -File deploy.ps1
# -> C:\Quantower\Settings\Scripts\Strategies\Hybrides ; REDÉMARRER Quantower (piège 5).
```

**Lancer en SHADOW (maintenant, gratuit)** : panneau Strategies → une hybride → symbole
NQ front → « Mode SHADOW » coché (défaut) → Run. Compte inutile. Laisser rouler les
séances ; les journaux s'écrivent dans `H:\IndicesBoursiers\automatisation\journaux\`.

**Si le Simulator est acheté un jour** (⚠ l'essai 7 jours est mort — pack Advanced
Features ou All-in-One, garantie 10 j) : panneau Trading Simulator ouvert (⚠ compte NEUF
à chaque ouverture), décocher le mode shadow, choisir le compte Simulator → dérouler
d'abord la sonde, puis les 3 hybrides en séance.

### Procédure manuelle « où vit le stop » (pendant l'étape A de la sonde)

1. Lancer la sonde ; attendre le verdict `A1 bracket visible`.
2. FERMER Quantower (croix, pas Stop) avant la 2e modification.
3. Rouvrir, reconnecter, rouvrir le panneau Simulator : le stop a-t-il survécu ?
   (⚠ compte neuf au redémarrage du panneau — l'observation vaut ce qu'elle vaut, c'est
   UN élément ; la vraie réponse = support Quantower, question en stand-by.)

## À SONDER (ce que la compile ne prouve pas — la sonde et la 1re séance trancheront)

0. ✅✅ **CONFIRMATION VALIDÉ DE BOUT EN BOUT le 2026-07-22.** Test 1 : affichage + clic →
   `ActionOnConfirm` (bouton de test, shadow, sans ordre). **Test 2 : clic → VRAI ORDRE sur
   Apex (MNQ ×1) + bracket SL/TP attaché correctement** — la chaîne live est prouvée en réel.
   Rappel du **bouton de test SANS RISQUE** (pour re-vérifier le mécanisme n'importe quand) : cocher
   **« Test : pop-up de confirmation au démarrage (aucun ordre) »** et lancer la stratégie
   **en mode SHADOW** (aucun compte requis). Au démarrage, un pop-up apparaît ; le « OK » ne
   fait que journaliser (`✅ TEST : pop-up CONFIRMÉ par clic`). Ça valide TOUT le mécanisme —
   affichage + clic → `ActionOnConfirm` — sans aucun ordre. Laisser expirer valide l'affichage
   + l'expiration. **Ensuite seulement**, pour prouver le clic → ordre RÉEL : mode
   CONFIRMATION sur **MNQ ×1** (2 $/pt), « Autoriser un compte réel » coché, en surveillant.
   ✅ **H2 en CONFIRMATION réglé le 2026-07-22** : le stop suiveur s'applique désormais
   AUTOMATIQUEMENT (resserrement = réduction du risque), le flat aussi ; seules les entrées
   et sorties sur signal demandent un clic → plus de pop-up par barre. Interprétation Apex à
   confirmer par l'utilisateur (l'humain initie chaque position, la protection est auto).
1. ✅ `SlTpHolder` en `Offset` = **ticks** depuis le fill — CONFIRMÉ au Test 2 (SL/TP posés à
   la bonne distance sur Apex).
2. ✅ Le bracket **S'ATTACHE** à l'ordre market sur Rithmic/Apex — CONFIRMÉ au Test 2. (Le sort
   du bracket après `Position.Close()` — annulé ? — reste à observer, mais mineur.)
3. L'ordre des événements `PositionRemoved` / `TradeAdded` (la raison de sortie « AUTRE »
   dans le journal = fill arrivé après ; compté prudemment côté garde-fou).
4. `[InputParameter]` de type `Account` dans le panneau (quasi certain — étude §2).
5. Fidélité des fills du Simulator (délai, slippage) — borne ce que la phase 4 conclut.
