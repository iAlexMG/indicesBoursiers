# Indices boursiers (ES / NQ) — de la donnée à l'exécution

Chaîne complète order-flow sur futures d'indices (CME : ES, NQ), en 4 piliers :

| Pilier | Contenu |
|---|---|
| `historique/` | Extraction des ticks (NqExtractor, Quantower) → OHLCV normalisé |
| `affichage/` | Dashboard order-flow futures ES/NQ (pyqtgraph, données Interactive Brokers) |
| `backtesting/` | Stratégies LEAN sur NQ |
| `automatisation/` | Quantower — simulation d'abord (compte réel privé, plus tard) |
