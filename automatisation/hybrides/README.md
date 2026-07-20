# Hybrides — le code LIVE des 3 stratégies + la sonde « Ordres Probe (SIM) »

> Écrit le 2026-07-20 (volet « code live » du chantier automatisation). Specs :
> [`../docs/strategies-hybrides.md`](../docs/strategies-hybrides.md) · étude Simulator :
> [`../docs/etude-simulator.md`](../docs/etude-simulator.md) · jumeaux backtest (volet C) :
> `../../backtesting/backtests/algorithms/{orb,sma_suiveur,rsi_bracket}_nq.py`.
> 🎯 Rappel du cadrage : **preuve de concept et de fonctionnalités, PAS la rentabilité.**

**UNE DLL (`Hybrides.dll`, net10.0), quatre stratégies** dans le panneau Strategies :

| Classe | Nom dans Quantower | Prouve | Jumeau LEAN |
|---|---|---|---|
| `OrbHybride` | Hybride H1 ORB (NQ) | le **bracket** posé au fill | `orb_nq.py` |
| `SmaSuiveurHybride` | Hybride H2 SMA Suiveur (NQ) | la **modification** d'ordre (suiveur) | `sma_suiveur_nq.py` |
| `RsiBracketHybride` | Hybride H3 RSI Bracket (NQ) | l'**annulation** du bracket (RSI 50) | `rsi_bracket_nq.py` |
| `OrdresProbe` | Ordres Probe (SIM) | le cycle de vie des ordres (étude §9) | — |

## Sécurité — les garde-fous d'abord

- **Garde anti-compte-réel** : les 3 stratégies REFUSENT de démarrer si la connexion du
  compte n'est pas `TradingSimulator`, sauf si « Autoriser un compte réel (phase 5) » est
  coché. **La sonde, elle, n'a AUCUN paramètre d'évasion** (codé en dur).
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

Résultat (2026-07-20, fenêtre 06-01 → 07-10) : **3 799 comparaisons, 10 écarts, TOUS dans
les 5 premières heures du 1er juin** (amorçage de Wilder, max 0,35 pt de RSI, éteint en
~90 min). Au-delà de l'amorçage : parité exacte à l'arrondi près. Avec le seed de 48 h,
l'amorçage du live est consommé bien avant toute décision de séance.

## Déployer & lancer

```powershell
powershell -ExecutionPolicy Bypass -File deploy.ps1
# -> C:\Quantower\Settings\Scripts\Strategies\Hybrides ; REDÉMARRER Quantower (piège 5).
```

Séquence du POC (décisions user du 2026-07-19) : **tout préparer AVANT d'activer l'essai
7 jours** (le compteur part au premier jour). Puis : panneau Trading Simulator ouvert
(⚠ compte NEUF à chaque ouverture du panneau), panneau Strategies → choisir symbole NQ +
compte Simulator → Run. Dérouler d'abord la sonde, puis les 3 hybrides en séance.

### Procédure manuelle « où vit le stop » (pendant l'étape A de la sonde)

1. Lancer la sonde ; attendre le verdict `A1 bracket visible`.
2. FERMER Quantower (croix, pas Stop) avant la 2e modification.
3. Rouvrir, reconnecter, rouvrir le panneau Simulator : le stop a-t-il survécu ?
   (⚠ compte neuf au redémarrage du panneau — l'observation vaut ce qu'elle vaut, c'est
   UN élément ; la vraie réponse = support Quantower, question en stand-by.)

## À SONDER (ce que la compile ne prouve pas — la sonde et la 1re séance trancheront)

1. `SlTpHolder` en `Offset` = ticks depuis le fill (sémantique attendue ; étape A vérifie
   « SL posé à ~20 ticks »).
2. Le sort du bracket après `Position.Close()` (attendu : annulé ; étape A3 le mesure).
3. L'ordre des événements `PositionRemoved` / `TradeAdded` (la raison de sortie « AUTRE »
   dans le journal = fill arrivé après ; compté prudemment côté garde-fou).
4. `[InputParameter]` de type `Account` dans le panneau (quasi certain — étude §2).
5. Fidélité des fills du Simulator (délai, slippage) — borne ce que la phase 4 conclut.
