# Orderflow — track scalping tick/carnet (réorientation 2026-07-10)

Nouvelle direction demandée par l'utilisateur : **scalping / orderflow temps réel**, pas des
stratégies bar-based lentes (les 8 stratégies portées en Phases 1-3 restent valides mais sont du
swing/bar, pas du scalping). Méthode inchangée : **mesurer d'abord**.

## Réalités posées (mentor)

- **« HFT » littéral (microsecondes, colocation) : non** via Quantower/Rithmic (latence API
  managée, pas de colo, règles propfirm). **Scalping orderflow (secondes → minutes) : oui.**
- **Backtest orderflow ≠ backtest de barres** : on a l'historique ticks+aggressor (delta/CVD/
  absorption backtestables), mais **pas** d'historique du carnet (DOM). Validation surtout en
  **forward/shadow test**.
- Ce qu'on a déjà et qui sert : ticks bruts + **aggressor 100 %** (Phase 0), **footprint maison**
  (indicateur VP delta), accès **temps réel** (`Symbol.NewLast/NewQuote/NewLevel2`, `DepthOfMarket`).

## `Orderflow Probe (NQ)` — sonde de faisabilité (Phase 0 bis)

Strategy qui souscrit N secondes au flux live et écrit `docs/orderflow-probe.md` :
- **Tape (Last)** : débit ticks/s, aggressor peuplé EN DIRECT (%), latence estimée.
- **Quote** : débit du meilleur bid/ask, dernier échantillon.
- **Level2 / DOM** : débit des updates + **profondeur du carnet disponible** (ou indisponible/payant)
  — c'est LA question qui conditionne quels signaux orderflow sont possibles.

**Lancer** : Quantower connecté Rithmic → panneau Strategies → `Orderflow Probe (NQ)` → symbole NQ →
Start. Elle s'arrête seule après la durée et écrit le rapport.

→ **Fini quand** : `docs/orderflow-probe.md` répond : ticks/s, aggressor live, DOM dispo + profondeur.
Ces mesures déterminent le moteur de signaux de scalping (CVD, imbalance, absorption, DOM).

**Résultat sonde** : aggressor 100 % live, **DOM disponible et profond** (984 bid/815 ask, gratuit),
quote ~30/s, tape 1 tick/s (overnight — à re-mesurer en RTH). → les 2 familles de signaux sont possibles.

## `CVD Orderflow (NQ)` — 1er moteur de signaux orderflow (temps réel)

Indicateur **sans chandelles** (fenêtre séparée) :
- **CVD** (Cumulative Volume Delta) : delta d'agression (achat−vente) issu des TICKS, seed depuis
  `GetTickHistory` puis **incrément tick par tick en direct** (`Symbol.NewLast`). Cumulé, remis à
  zéro **par session** (ou jour / jamais). Résolution paramétrable (15 s → 1 h).
- **Imbalance du carnet** : taille bid vs ask sur N niveaux, lue en direct sur le DOM
  (`Symbol.NewLevel2` + `DepthOfMarket`), affichée (vert=acheteur / rouge=vendeur).

**Lancer** : graphe NQ → *Add indicator* → `CVD Orderflow (NQ)` (fenêtre séparée en bas). Le CVD se
seed sur l'historique tick puis vit en direct ; l'imbalance ne s'affiche qu'avec du flux (heures de marché).

Prochaines briques orderflow : **absorption** (prix cale vs delta pousse), **vitesse du tape**,
signaux d'entrée/sortie scalping → stratégie shadow.
