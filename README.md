# Indices boursiers (ES / NQ) — de la donnée à l'exécution

Chaîne complète order-flow sur futures d'indices (CME : ES, NQ), en 4 piliers :

| Pilier | Contenu |
|---|---|
| `historique/` | Extraction des ticks (NqExtractor, Quantower) → OHLCV normalisé |
| `affichage/` | Dashboard order-flow futures ES/NQ (pyqtgraph — Quantower/Rithmic ; IBKR gardé en témoin dégradé) |
| `backtesting/` | Les 8 stratégies LEAN rejouées sur NQ en 1 m (miroir du frère crypto, verdict hors-échantillon) |
| `automatisation/` | Quantower — simulation d'abord (compte réel privé, plus tard) |
