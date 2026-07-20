# Phase 2 — Backtests NQ des 8 stratégies (moteur LEAN)

> Statut : **TERMINÉE** (run 2026-07-08). Les 8 stratégies du projet frère rejouées sur les
> données NQ collectées en Phase 1, moteur LEAN natif. Le code vit dans
> `../../backtesting/backtests/algorithms/` (voir « Reproduire » plus bas).

> **MAJ 2026-07-19 — ce document décrit la campagne 1H ; le banc a depuis été rejoué en
> 1 MINUTE** (re-calibrage v2 anti-churn du frère, fenêtre 2026-06-01→07-10, jugement
> hors-échantillon mi-fenêtre). Les chiffres 1H ci-dessous restent la démonstration de la
> Phase 2 ; les résultats à jour vivent dans `../../backtesting/site-content/contenu.json`
> et sur le site (pilier Backtesting, 8 fiches). Recette du run 1 m :
> `../../backtesting/README.md`.

## Ce qui change vs le monde crypto (mesuré, pas supposé)

- **Instrument** : `SymbolProperties` NQ = tick 0,25 / multiplicateur **20** / lot **1** (contrats
  entiers). Multiplicateur vérifié par le « sol de vérité » de `buyhold_nq` : équité LEAN
  = `cash + qté × close × 20` au dollar près.
- **Frais** : `FeeModel` **commission par contrat / par side = 2,00 $** (≈ 4 $ aller-retour).
  ⚠️ ordre de grandeur — **à remplacer par le tarif réel du plan Rithmic** (à mesurer).
  Remplace le `0,04 % du notionnel` crypto.
- **Sizing** : `market_order` en **contrats entiers** (pas le `set_holdings` fractionnaire). Les
  stratégies long/flat tiennent **1 contrat** ; la n°7 dimensionne au risque puis arrondit (≥1).
- **Levier / marge** : 1 contrat NQ ≈ **600 000 $ de notionnel** → sur 100 000 $ de capital, c'est
  déjà **~6× de levier**. Modélisé par `set_leverage(20)` (simplification ; la marge de la prop firm réelle
  et son trailing drawdown seront modélisés au risk manager de la Phase 5).

## Tableau de bord NQ (18 juin → 8 juillet 2026, ~20 jours, 337 barres 1H)

| # | Stratégie | Net | Drawdown | Ordres | Frais |
|---|---|---:|---:|---:|---:|
| 1 | Buy & Hold (référence) | **−21,36 %** | 30,0 % | 1 | 2 $ |
| 2 | SMA 50/200 | −13,33 % | 21,3 % | 2 | 4 $ |
| 3 | MACD 12/26/9 | −21,91 % | 32,5 % | 18 | 36 $ |
| 4 | RSI 14 (contre-tendance) | −1,68 % | 18,4 % | 3 | 6 $ |
| 5 | Bollinger 20/2σ | **+0,01 %** | 15,6 % | 15 | 30 $ |
| 6 | RSI 14 + stop/take | −1,68 % | 18,4 % | 3 | 6 $ |
| 7 | Stratégie avancée | −6,85 % | **8,2 %** | 2 | 4 $ |
| 8 | **Volume Profile** (vah_break + delta) | **+2,87 %** | **0,9 %** | 6 | 12 $ |

## Écarts vs BTC (dashboard frère, 6 mois baissier) — commentés

| Stratégie | BTC (6 mois) | NQ (20 j) | Lecture |
|---|---:|---:|---|
| Buy & Hold | −33,2 % | −21,4 % | NQ n'a baissé que **−3,5 % en prix** — mais 1 contrat = ~6× levier → −21 % sur le compte |
| SMA 50/200 | −4,3 % | −13,3 % | fenêtre trop courte : SMA 200 = ~8 j de warmup → ne trade que ~5 j |
| MACD | −32,1 % | −21,9 % | sur-trading identique (18 ordres) ; pire directionnel |
| RSI | −26,4 % | −1,7 % | NQ bien moins volatil → moins de couteaux qui tombent |
| Bollinger | −31,5 % | +0,0 % | quasi neutre |
| RSI + stop/take | −29,0 % | −1,7 % | **identique au RSI nu** → les stops % (8/10 %) ne se déclenchent JAMAIS sur NQ |
| Stratégie avancée | −3,3 % (dd 8 %) | −6,9 % (dd 8,2 %) | garde son profil « contrôle de la casse » (dd le plus bas des directionnels) |
| Volume Profile | −2,5 à −15,7 % | **+2,9 % (dd 0,9 %)** | **la mieux portée** : VP par session épouse les heures CME nativement |

**Quatre enseignements majeurs :**

1. **Le levier est le vrai risque, pas la direction.** NQ n'a bougé que −3,5 % en prix sur la
   période, mais 1 contrat sur 100 k$ = ~6× → le compte fait −21 %. Pour le challenge prop firm
   (trailing drawdown intrajournalier), **c'est décisif** → argument fort pour **MNQ** (÷10).
2. **Les stops en % ne conviennent pas à NQ.** RSI et RSI+stop donnent le **résultat identique**
   (les seuils 8 %/10 % calibrés crypto ne bindent jamais sur des barres NQ). Il faudra
   recalibrer les stops en **ATR / points / ticks** (déjà le cas de la n°7, en ATR → elle, marche).
3. **Les frais changent de nature.** Fixe/contrat (2 $) vs % du notionnel : sur BTC, le MACD
   payait 11 692 $ de frais (la tyrannie du sur-trading) ; sur NQ à 1 contrat, 36 $. Négligeable
   ici — mais croît linéairement avec le nombre de contrats.
4. **Fenêtre courte = résultats indicatifs, pas robustes.** 20 jours vs 6 mois. Les stratégies à
   long lookback (SMA 200, avancée) ont à peine de quoi trader. Le tableau se **stabilisera** à
   mesure que la collecte quotidienne (Phase 1) accumule l'historique.

## Verdict

Le portage est **fidèle et fonctionnel** : mêmes signaux, moteur LEAN natif, chaîne données
100 % réutilisée. Comme sur BTC, **aucun suiveur de tendance ne gagne** sur cette fenêtre ; la
**Volume Profile par session** et le **retour à la moyenne** s'en sortent le mieux, et la
**stratégie avancée** garde le plus faible drawdown directionnel. Les vraies différences NQ
(levier, stops en points, frais/contrat) sont identifiées et chiffrées — matière directe pour
les Phases 3-5.

## Reproduire

```powershell
# 1) données NQ canoniques (depuis la base de ticks de la Phase 1)
python historique/normalize_ohlcv.py --dir H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09 --prefix NQ-CME
python backtesting/backtests/volume_profile_features.py --db H:/IndicesBoursiers/historique/NQ-2026-09.db `
    --out H:/IndicesBoursiers/historique/ohlcv/NQ-2026-09/features_vp.csv --tick 5

# 2) un backtest (depuis backtests/lean/Launcher/bin/Release)
$env:PYTHONNET_PYDLL="C:\Users\Moi\anaconda3\envs\backtesting\python311.dll"
$env:PYTHONHOME="C:\Users\Moi\anaconda3\envs\backtesting"
dotnet QuantConnect.Lean.Launcher.dll --algorithm-language Python `
    --algorithm-type-name VolumeProfileNq `
    --algorithm-location "../../../../algorithms/volume_profile_nq.py"
```

Algos NQ : `../../backtesting/backtests/algorithms/` (module partagé `nq_instrument.py` +
`buyhold_nq`, `sma_croisement_nq`, `macd_nq`, `rsi_retour_moyenne_nq`, `bollinger_nq`,
`risque_stops_nq`, `strategie_avancee_nq`, `volume_profile_nq`).
