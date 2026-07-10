# Quantower — NQ live (Rithmic) : des ticks aux ordres

Portage vers **NQ (futures Nasdaq, CME)** de la chaîne construite et backtestée sur BTCUSDT
dans `../Backtesting` : extraction ticks → SQLite → OHLCV/footprint → backtests LEAN →
indicateurs & stratégies Quantower → shadow mode → ordres semi-automatisés sur challenge prop firm.

## Phases (ordre non négociable)

| Phase | Objet | Fini quand | Statut |
|---|---|---|---|
| 0 | POC & mesures de faisabilité (Rithmic **dans** Quantower) | `docs/phase0-poc.md` : 4 réponses mesurées | ✅ profondeur ~2 sem., aggressor 100 %, ~412 k ticks/j, tick 5 $ |
| 1 | Extracteur ticks NQ → SQLite (schéma identique au frère) | N jours en base + OHLCV 1H + chandelier HTML | ✅ 6,93 M ticks (20 j), OHLCV OK ; reste : automatiser |
| 2 | Backtests NQ des 8 stratégies (LEAN, vit dans `../Backtesting`) | tableau mesuré + écarts vs BTC commentés | ✅ 8/8 ; VP +2,9 % (dd 0,9 %) meilleure — [tableau](docs/phase2-backtests-nq.md) |
| 3 | Indicateurs C# Quantower + parité vs pipeline Python | indicateurs déployés + rapport de parité | ✅ SMA ([#1](docs/phase3-parity-sma.md)) · RSI ([#2](docs/phase3-parity-rsi.md)) · VP session/sous-VP ([#3](docs/phase3-parity-vp.md)) · EMA ([#4](docs/phase3-parity-ema.md)) · indicateur signaux (strat. n°7) |
| 4 | Shadow mode (signaux journalisés, ZÉRO ordre) | N jours de log + rapport de concordance | ⏳ |
| 5 | Ordres réels semi-automatisés (garde-fous d'abord) | 1 stratégie supervisée + journal d'exécution | ⏳ |

## Arborescence

```
poc/          Phase 0 : stratégie de mesure DANS Quantower (Phase0Strategy) + console diagnostic API (Phase0Poc)
extractor/    Phase 1 : extracteur incrémental ticks NQ → F:\data\NQ-<contrat>.db
indicators/   Phase 3 : indicateurs C# Quantower (VP session, delta EMA24, signaux par stratégie)
strategies/   Phase 4-5 : stratégies Quantower (shadow puis exécution avec risk manager)
docs/         journal.md (décisions datées) + rapports de phase
```

## Comprendre le projet (docs pédagogiques)

- [docs/resume-phases.md](docs/resume-phases.md) — les 6 phases et les 8 stratégies de trading, en langage simple.
- [docs/guide-architecture-quantower.md](docs/guide-architecture-quantower.md) — indicateur vs stratégie, Visual Studio vs `dotnet` (pourquoi le code actuel diffère des projets précédents).

## Conventions verrouillées

- **Tout en UTC**, tz-aware ; sessions en heure de New York (Asia 18:00–02:59 /
  London 03:00–09:29 / NY 09:30–16:59, pause CME 17:00–17:59 exclue, DST géré).
- **Schéma SQLite identique** au frère : `trades(trade_id INTEGER PK, ts INTEGER ms UTC,
  price REAL, size REAL, side TEXT buy/sell agresseur)` + `_meta` (symbol, exchange,
  tick_size, multiplier, source=rithmic).
- **Données sur `F:\data\`** ; credentials en config locale gitignorée.
- **DLL Quantower** : résolution **dynamique** du dossier `C:\Quantower\TradingPlatform\v*`
  le plus récent (jamais de chemin en dur, jamais versionnée, `Private=false`).
- Contrat NQ : tick 0,25 pt = 5 $ (20 $/pt) ; MNQ (2 $/pt) pour les premiers ordres.
- **C#** pour tout ce qui vit dans Quantower ; **Python** réutilisé pour la chaîne
  données/backtests existante du frère.
