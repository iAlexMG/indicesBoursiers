# Extractor — ticks & barres NQ (Rithmic) → SQLite

Deux stratégies Quantower dans la même DLL (`NqExtractor/`) :

| Stratégie | Donnée | Profondeur | Base produite |
|---|---|---|---|
| **NQ-ES History Ticks** | ticks Last (avec agresseur) | ~2-3 semaines (limite Rithmic) | `H:\IndicesBoursiers\historique\NQ-<contrat>.db` |
| **NQ-ES History Bars 1m** | barres minute (OHLCV + ticks) | **toute la profondeur serveur** (celle du graphique) ⚠️ voir la mesure ci-dessous | `H:\IndicesBoursiers\historique\NQ-<contrat>-1m.db` |

Les ticks servent au footprint / volume profile (agresseur requis) ; les barres minute
étendent l'OHLCV loin en arrière pour les backtests. Fusion des deux par
`normalize_ohlcv.py` (les lignes issues des ticks restent prioritaires), qui produit
`{1m,1H,4H,D}.csv` — le **1m** est le CSV que consomment les backtests LEAN :

```bash
python normalize_ohlcv.py --dir H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09 --prefix NQ-CME ^
                          --bars-db H:\IndicesBoursiers\historique\NQ-2026-09-1m.db
```

## NQ-ES History Bars 1m (profondeur maximale)

> ⚠️ **« Toute la profondeur serveur » MESURÉE le 2026-07-19 (NQ-2026-09)** : avant fin
> janvier 2026, Rithmic ne sert qu'**un jour-échantillon par mois** (~120 barres) — les
> 56 mois « ingérés » de 2020-08 à 2026-01 sont des miettes, pas de l'historique continu.
> La vraie profondeur exploitable commence au **2026-01-27**. Toute fenêtre de backtest
> doit se choisir dans cette plage (le rebuild 1 m utilise 2026-06-01→07-10, alignée sur
> le frère crypto).

Au premier run (base vide), la stratégie **sonde vers l'arrière** mois par mois depuis le
mois courant, jusqu'à N mois vides consécutifs (défaut 3) ou la borne « Sonde max » (défaut
6 ans) — le journal affiche alors la **profondeur réellement servie** par Rithmic (« plus
ancien mois servi = … »). Les runs suivants ne re-téléchargent que le mois courant et les
mois manquants (`_ingested` par mois, idempotent, `INSERT OR REPLACE` sur `ts`). Même mode
démon « toutes les N heures » que l'extracteur de ticks.

Schéma : `bars(ts PK ms UTC ouverture, open, high, low, close, volume, ticks)` + `_meta`
(`period_min=1`) + `_ingested('month/YYYY-MM')`.

⚠️ Pas de côté agresseur dans une barre : `buy_volume` reste vide sur les lignes issues des
barres, et `features_vp.csv` (volume profile) reste borné à la fenêtre de ticks.

> Build : Quantower **v1.146.14+** tourne sous **.NET 10** → le projet cible `net10.0`
> (mesuré 2026-07-10 ; v1.145.x était net8.0). `deploy.ps1` choisit le TFM le plus récent.

# NQ-ES History Ticks — ticks NQ (Rithmic) → SQLite (Phase 1)

Stratégie Quantower (`NQ-ES History Ticks`) qui télécharge les ticks NQ via la connexion Rithmic
**déjà authentifiée dans Quantower** et les écrit dans `H:\IndicesBoursiers\historique\NQ-<contrat>.db`, au **schéma
exact** du projet frère → la chaîne Python aval (`candles.py`, `volume_profile_features.py`)
tourne sans modification.

## Pourquoi une stratégie (et pas un script Python REST) ?

Rithmic ne fournit pas d'API publique hors Quantower, et le mot de passe stocké n'est pas
déchiffrable hors process (mesuré Phase 0). L'accès aux ticks passe donc par le BusinessLayer,
**dans** Quantower.

## Schéma produit (identique au frère)

```sql
trades(trade_id INTEGER PRIMARY KEY,  -- rowid, insertion append = ordre chronologique
       ts INTEGER,                    -- ms UTC
       price REAL, size REAL,
       side TEXT)                     -- 'buy'/'sell' = côté agresseur (AggressorFlag)
_meta(k,v)        -- symbol=NQ, market/exchange=CME, contract, expiration, tick_size=0.25,
                  --   multiplier=20, tick_value=5, source=rithmic
_ingested(name,rows,at)   -- 'day/YYYY-MM-DD' pour les jours complets déjà collectés
idx_trades_ts ON trades(ts)
```

Détail mesuré : pas de `TradeId` dans l'historique Rithmic → `trade_id` = rowid auto. Les ticks
sans côté agresseur (0,0002 % mesuré) sont exclus (jamais de `side` vide).

## Incrémental & idempotent

- Les **jours complets passés** sont marqués dans `_ingested` et jamais re-téléchargés.
- Le **jour courant** (partiel) est purgé puis ré-inséré à chaque run → relancer est sûr.
- **Sonde arrière** (ajoutée 2026-07-10) : au premier run après cette version, la stratégie
  descend jour par jour **sous** le plus ancien tick en base et aspire tout ce que Rithmic
  sert encore, jusqu'à N jours vides consécutifs (défaut 7 — couvre week-ends et fériés) ou
  la borne « Sonde arrière max » (défaut 90 j). La profondeur mesurée est journalisée
  (« PROFONDEUR TICKS MESURÉE ») et mémorisée dans `_meta(tick_probe_oldest)` → la sonde ne
  retourne plus en arrière ensuite (fenêtre Rithmic glissante : l'ancien ne réapparaît pas).
  Après un backfill arrière, la table est **réordonnancée** (rowid croissant = chronologique,
  le contrat de `candles.py`/`features_vp`).

## Collecte automatique (démon)

Paramètre **« Collecte auto toutes les N heures »** (défaut **6**, `0` = one-shot) : une fois
démarrée, la stratégie recollecte toute seule à cet intervalle **tant qu'elle tourne** (Quantower
ouvert + Rithmic connecté). Comme l'extraction est idempotente, la relancer souvent ne coûte que
le jour courant. ⚠️ La collecte n'a lieu **que quand Quantower est ouvert** (la connexion Rithmic
n'existe que là) — mais l'usage quotidien pour le challenge suffit, et le tampon ~2 semaines
couvre les week-ends/coupures : au redémarrage, la 1re passe rattrape les jours manqués (dans la
limite de `MaxBackfillDays`).

**Mise en place (une fois)** : dans Quantower, démarrer `NQ-ES History Ticks` (symbole NQ), le
laisser en **Working**, puis **sauvegarder le workspace** pour qu'il persiste. Les jours suivants,
il suffit d'ouvrir Quantower ; relancer la stratégie si le workspace ne l'a pas rouverte en Working.

## Déployer & lancer

```powershell
powershell -File extractor\NqExtractor\deploy.ps1   # build + copie dans Settings\Scripts\Strategies
```
Puis dans Quantower (connecté Rithmic) : panneau **Strategies** → `NQ-ES History Ticks` →
paramètre **Symbole = NQ** (contrat front) → **Start**. La stratégie s'arrête seule à la fin ;
suivre l'onglet Logs. Résultat : `H:\IndicesBoursiers\historique\NQ-2026-09.db`.

## Vérifier / exploiter (Python, projet frère)

```powershell
# chandelles + graphique de contrôle
python ..\..\Backtesting\history_extractor\candles.py --db H:\IndicesBoursiers\historique\NQ-2026-09.db --chart nq.html --open
```
