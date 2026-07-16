using System.Globalization;
using System.Text;
using TradingPlatform.BusinessLayer;

namespace NqFeed;

/// <summary>
/// Phase 0 — SONDE du flux temps réel NQ (Rithmic), à passer AVANT d'écrire la moindre vue.
///
/// Elle ne produit rien : elle mesure. La question qui commande tout le pilier est celle de
/// l'entitlement Level 2 — la heatmap et le DOM en dépendent entièrement, et aucune lecture de
/// la DLL ne peut y répondre (l'API expose `Symbol.NewLevel2` que l'abonnement Rithmic couvre
/// la profondeur ou non). On branche, on regarde, on saura. Même démarche que la Phase 0 du
/// pilier Historique, qui avait mesuré la profondeur réellement servie plutôt que de la supposer.
///
/// L'abonnement se déclenche par la seule attache des handlers : `SubscribeAction` est interne
/// à la plateforme, les accesseurs `add_NewLast` / `add_NewLevel2` sont publics et suffisent
/// (mesuré par réflexion sur BusinessLayer v1.146.14).
///
/// Ce qu'elle rapporte, dans l'onglet Logs :
///   1. L2 servi ou non — le verdict qui décide des vues DOM &amp; Heatmap ;
///   2. la profondeur réelle du carnet (niveaux bid/ask) et l'étalement en ticks ;
///   3. la couverture de l'AggressorFlag sur les trades (le footprint en dépend) ;
///   4. les cadences (trades/s, updates L2/s) — dimensionnent le pont de la Phase 1 ;
///   5. le décalage d'horloge et le retard de cotation (flux différé ou non).
/// </summary>
public sealed class NqFeedProbeStrategy : Strategy
{
    [InputParameter("Symbole (NQ front)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Durée de la sonde (secondes)", 1, 10, 900, 1, 0)]
    public int ProbeSeconds = 60;

    // Défaut porté à 200 après le run du 2026-07-15 : 30 niveaux étaient TOUS remplis (30×30,
    // 0 snapshot vide) — la profondeur réelle servie par Rithmic était donc masquée par la demande.
    [InputParameter("Niveaux DOM demandés", 2, 1, 500, 1, 0)]
    public int LevelsCount = 200;

    [InputParameter("Cadence des snapshots DOM (ms)", 3, 50, 5000, 50, 0)]
    public int SnapshotMs = 250;

    private readonly object _lock = new();
    private System.Threading.Timer? _domTimer;
    private System.Threading.Timer? _endTimer;
    private DateTime _startedUtc;

    // -- trades (NewLast) --
    private long _lastCount, _aggBuy, _aggSell, _aggNone, _aggNotSet, _tradeIdSeen;
    private double _clockSkewSumMs;

    // -- carnet (NewLevel2) --
    private long _l2Count, _l2Bid, _l2Ask, _l2Closed, _l2WithOrderCount;
    private readonly HashSet<string> _l2Ids = new();
    private bool _l2IdsSaturated;

    // -- carnet agrégé (DepthOfMarket) --
    private long _domSnaps, _domEmpty;
    private int _domBidsMax, _domAsksMax;
    private double _domSpanTicksMax;

    public NqFeedProbeStrategy() => Name = "NQ Feed Probe";

    protected override void OnRun()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné (choisir NQ front)."); this.Stop(); return; }

        _startedUtc = DateTime.UtcNow;
        this.LogInfo($"SONDE {ProbeSeconds} s sur {s.Name} ({s.Id}) | tick={s.TickSize} "
                   + $"| retard de cotation annoncé = {s.QuoteDelay.TotalSeconds:0.#} s");
        if (s.QuoteDelay > TimeSpan.Zero)
            this.LogInfo("⚠ Le symbole annonce un RETARD de cotation : flux différé, pas temps réel.");

        // L'attache des handlers EST l'abonnement (SubscribeAction est interne à la plateforme).
        s.NewLast += OnNewLast;
        s.NewLevel2 += OnNewLevel2;

        var period = TimeSpan.FromMilliseconds(SnapshotMs);
        _domTimer = new System.Threading.Timer(_ => SnapshotDom(), null, period, period);
        _endTimer = new System.Threading.Timer(_ => { Report(); this.Stop(); }, null,
                                               TimeSpan.FromSeconds(ProbeSeconds), Timeout.InfiniteTimeSpan);
    }

    protected override void OnStop()
    {
        _domTimer?.Dispose(); _domTimer = null;
        _endTimer?.Dispose(); _endTimer = null;
        if (Instrument is { } s) { s.NewLast -= OnNewLast; s.NewLevel2 -= OnNewLevel2; }
    }

    /// <summary>Un trade. On mesure surtout la couverture de l'AggressorFlag : sans lui, le
    /// footprint retomberait sur la règle du tick — l'approximation qu'on subissait chez IBKR.</summary>
    private void OnNewLast(Symbol symbol, Last last)
    {
        lock (_lock)
        {
            _lastCount++;
            switch (last.AggressorFlag)
            {
                case AggressorFlag.Buy: _aggBuy++; break;
                case AggressorFlag.Sell: _aggSell++; break;
                case AggressorFlag.None: _aggNone++; break;
                default: _aggNotSet++; break;
            }
            if (!string.IsNullOrEmpty(last.TradeId)) _tradeIdSeen++;
            _clockSkewSumMs += (DateTime.UtcNow - last.Time.ToUniversalTime()).TotalMilliseconds;
        }
    }

    /// <summary>Une mise à jour du carnet. Le seul fait d'entrer ici répond à la question de
    /// l'entitlement L2. On compte les Id distincts pour distinguer un flux par ORDRE (MBO :
    /// beaucoup d'Id) d'un flux par NIVEAU de prix (MBP : autant d'Id que de niveaux).</summary>
    private void OnNewLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom)
    {
        lock (_lock)
        {
            _l2Count++;
            if (level2.PriceType == TradingPlatform.BusinessLayer.Integration.QuotePriceType.Bid) _l2Bid++; else _l2Ask++;
            if (level2.Closed) _l2Closed++;
            if (level2.NumberOrders > 0) _l2WithOrderCount++;
            if (!_l2IdsSaturated && !string.IsNullOrEmpty(level2.Id))
            {
                _l2Ids.Add(level2.Id);
                if (_l2Ids.Count >= 50_000) _l2IdsSaturated = true;   // borne mémoire : au-delà, MBO est acquis
            }
        }
    }

    /// <summary>Photographie le carnet agrégé par prix — exactement la forme que le pont de la
    /// Phase 1 poussera vers Python (`FlowStore.add_book` attend des niveaux (prix, taille)).</summary>
    private void SnapshotDom()
    {
        var s = Instrument;
        if (s is null) return;
        try
        {
            var p = new GetLevel2ItemsParameters
            {
                AggregateMethod = AggregateMethod.ByPriceLVL,
                LevelsCount = LevelsCount,
                CalculateCumulative = false,
            };
            var dom = s.DepthOfMarket?.GetDepthOfMarketAggregatedCollections(p);
            var bids = dom?.Bids ?? Array.Empty<Level2Item>();
            var asks = dom?.Asks ?? Array.Empty<Level2Item>();

            lock (_lock)
            {
                _domSnaps++;
                if (bids.Length == 0 && asks.Length == 0) { _domEmpty++; return; }
                if (bids.Length > _domBidsMax) _domBidsMax = bids.Length;
                if (asks.Length > _domAsksMax) _domAsksMax = asks.Length;

                double tick = s.TickSize;
                if (tick > 0 && bids.Length > 0 && asks.Length > 0)
                {
                    double span = (asks[^1].Price - bids[^1].Price) / tick;
                    if (span > _domSpanTicksMax) _domSpanTicksMax = span;
                }
            }
        }
        catch (Exception ex) { this.LogError($"Snapshot DOM : {ex.Message}"); }
    }

    private void Report()
    {
        lock (_lock)
        {
            double secs = Math.Max(1e-9, (DateTime.UtcNow - _startedUtc).TotalSeconds);
            var s = Instrument!;
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;

            sb.AppendLine();
            sb.AppendLine("==================== SONDE PHASE 0 — RÉSULTAT ====================");
            sb.AppendLine($"Symbole {s.Name} ({s.Id}) | fenêtre {secs.ToString("0.#", ci)} s | tick {s.TickSize.ToString(ci)}");
            sb.AppendLine();

            // --- 1. LE VERDICT ---
            bool l2 = _l2Count > 0;
            bool domOk = _domBidsMax > 0 || _domAsksMax > 0;
            sb.AppendLine($"[1] LEVEL 2 SERVI PAR RITHMIC : {(l2 || domOk ? "OUI" : "NON")}");
            if (!l2 && !domOk)
            {
                sb.AppendLine("    → aucun événement L2, carnet agrégé vide. La heatmap et le DOM resteraient");
                sb.AppendLine("      VIDES : l'entitlement de profondeur CME manque à l'abonnement Rithmic.");
            }
            else if (!l2)
            {
                sb.AppendLine("    → carnet agrégé peuplé SANS événement NewLevel2 : le DOM est lisible par");
                sb.AppendLine("      sondage, mais la heatmap devra s'appuyer sur les snapshots, pas sur le flux.");
            }

            // --- 2. PROFONDEUR ---
            sb.AppendLine();
            sb.AppendLine($"[2] PROFONDEUR DU CARNET : {_domBidsMax} bids × {_domAsksMax} asks (max observé, {LevelsCount} demandés)");
            sb.AppendLine($"    étalement max {_domSpanTicksMax.ToString("0.#", ci)} ticks | snapshots {_domSnaps} dont {_domEmpty} vides");
            if (_domBidsMax >= LevelsCount || _domAsksMax >= LevelsCount)
                sb.AppendLine($"    ⚠ plafonné par la demande : relancer avec « Niveaux DOM » > {LevelsCount} pour voir le vrai fond.");

            // --- 3. AGRESSEUR (le footprint en dépend) ---
            sb.AppendLine();
            long typed = _aggBuy + _aggSell;
            double cover = _lastCount > 0 ? 100.0 * typed / _lastCount : 0;
            sb.AppendLine($"[3] TRADES : {_lastCount} ({(_lastCount / secs).ToString("0.#", ci)}/s)");
            sb.AppendLine($"    AggressorFlag : Buy {_aggBuy} | Sell {_aggSell} | None {_aggNone} | NotSet {_aggNotSet}");
            sb.AppendLine($"    couverture du côté agresseur : {cover.ToString("0.###", ci)} %"
                        + (cover >= 99.9 ? "  → footprint exact, aucune inférence" : "  ⚠ inférence nécessaire sur le reliquat"));
            sb.AppendLine($"    TradeId fourni : {(_tradeIdSeen > 0 ? $"OUI ({_tradeIdSeen}/{_lastCount})" : "NON → dédup par (ts, prix, taille)")}");

            // --- 4. CADENCES (dimensionnent le pont) ---
            sb.AppendLine();
            sb.AppendLine($"[4] UPDATES L2 : {_l2Count} ({(_l2Count / secs).ToString("0.#", ci)}/s) | bid {_l2Bid} / ask {_l2Ask} | fermetures {_l2Closed}");
            if (_l2Count > 0)
            {
                string ids = _l2IdsSaturated ? "≥ 50 000 (saturé)" : _l2Ids.Count.ToString(ci);
                // MBO vs MBP se tranche par les FERMETURES, pas par le volume d'Id : en MBO chaque
                // fermeture retire un ordre unique, donc les Id distincts sont AU MOINS aussi nombreux
                // que les fermetures. Moins d'Id que de fermetures = Id réutilisés = niveaux de prix.
                // (Un seuil naïf « beaucoup d'Id → MBO » se trompe : les niveaux dérivent avec le prix.)
                string kind = _l2IdsSaturated ? "PAR ORDRE (MBO)"
                            : _l2Closed > 0 && _l2Ids.Count < _l2Closed ? "PAR NIVEAU DE PRIX (MBP) — Id réutilisés"
                            : _l2Closed == 0 ? "indéterminé (aucune fermeture observée)"
                            : "PAR ORDRE (MBO) — Id jamais réutilisés";
                sb.AppendLine($"    Id distincts : {ids} pour {_l2Closed} fermetures → flux {kind}");
                sb.AppendLine($"    NumberOrders renseigné : {(_l2WithOrderCount > 0 ? "OUI (ordres empilés par prix → confirme MBP)" : "NON")}");
            }

            // --- 5. HORLOGE ---
            sb.AppendLine();
            double skew = _lastCount > 0 ? _clockSkewSumMs / _lastCount : double.NaN;
            sb.AppendLine($"[5] HORLOGE : décalage moyen (local − horodatage) = {(double.IsNaN(skew) ? "n/a" : skew.ToString("0", ci) + " ms")}");
            sb.AppendLine($"    retard de cotation annoncé : {s.QuoteDelay.TotalSeconds.ToString("0.#", ci)} s");
            if (!double.IsNaN(skew) && Math.Abs(skew) > 60_000)
                sb.AppendLine("    ⚠ décalage > 1 min : vérifier le fuseau (piège UTC+8 déjà rencontré côté Bitget/OKX).");

            sb.AppendLine("==================================================================");

            // La grille de logs de Quantower n'affiche que la PREMIÈRE ligne d'un message : un
            // rapport multi-ligne y est tronqué à l'écran (le texte complet ne survit que dans
            // Settings\Scripts\ScriptsData\<stratégie>\logs\*.slog). D'où une ligne = un message.
            foreach (var line in sb.ToString().Split('\n'))
                this.LogInfo(line.TrimEnd('\r'));
        }
    }
}
