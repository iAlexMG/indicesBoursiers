# Extractor — ticks NQ (Rithmic) → SQLite (Phase 1)

Stratégie Quantower (`NQ Tick Extractor`) qui télécharge les ticks NQ via la connexion Rithmic
**déjà authentifiée dans Quantower** et les écrit dans `F:\data\NQ-<contrat>.db`, au **schéma
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
- Base vide → backfill des `MaxBackfillDays` derniers jours (≤ ~2 sem. de profondeur Rithmic).

## Collecte automatique (démon)

Paramètre **« Collecte auto toutes les N heures »** (défaut **6**, `0` = one-shot) : une fois
démarrée, la stratégie recollecte toute seule à cet intervalle **tant qu'elle tourne** (Quantower
ouvert + Rithmic connecté). Comme l'extraction est idempotente, la relancer souvent ne coûte que
le jour courant. ⚠️ La collecte n'a lieu **que quand Quantower est ouvert** (la connexion Rithmic
n'existe que là) — mais l'usage quotidien pour le challenge suffit, et le tampon ~2 semaines
couvre les week-ends/coupures : au redémarrage, la 1re passe rattrape les jours manqués (dans la
limite de `MaxBackfillDays`).

**Mise en place (une fois)** : dans Quantower, démarrer `NQ Tick Extractor` (symbole NQ), le
laisser en **Working**, puis **sauvegarder le workspace** pour qu'il persiste. Les jours suivants,
il suffit d'ouvrir Quantower ; relancer la stratégie si le workspace ne l'a pas rouverte en Working.

## Déployer & lancer

```powershell
powershell -File extractor\NqExtractor\deploy.ps1   # build + copie dans Settings\Scripts\Strategies
```
Puis dans Quantower (connecté Rithmic) : panneau **Strategies** → `NQ Tick Extractor` →
paramètre **Symbole = NQ** (contrat front) → **Start**. La stratégie s'arrête seule à la fin ;
suivre l'onglet Logs. Résultat : `F:\data\NQ-2026-09.db`.

## Vérifier / exploiter (Python, projet frère)

```powershell
# chandelles + graphique de contrôle
python ..\..\Backtesting\history_extractor\candles.py --db F:\data\NQ-2026-09.db --chart nq.html --open
```
