using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TradingPlatform.BusinessLayer;

namespace NqFeed;

/// <summary>
/// Phase 1 — LE PONT. Sert le flux temps réel NQ (Rithmic) à un client Python sur une socket TCP
/// locale, en NDJSON (une ligne = un message JSON).
///
/// Pourquoi un pont plutôt qu'une lecture directe — la vraie raison, plus nuancée que « c'est
/// impossible ». Mesuré par `automatisation/poc/Phase0Poc` (voir docs/phase0-poc.md, Q1) :
///   - `Core.Instance` + `Core.Initialize()` FONCTIONNENT hors du process Quantower (il faut un
///     resolver d'assemblies vers bin\, bin\System\, bin\runtimes\… et CurrentDirectory sur le
///     bin). 80 connexions sont lues depuis settings.xml, dont Rithmic ; user et serveur sont
///     restaurés tout seuls.
///   - LE SEUL VERROU : le mot de passe stocké est chiffré et `FailedToRestorePassword=True`
///     hors process -> `Connect()` rend `State=Fail, "Password is empty."`.
/// Donc ce n'est pas le BusinessLayer qui interdit le standalone, c'est le SECRET. La sortie
/// existe (mode `rithmic` du POC : mot de passe en clair dans un `credentials.local.json`
/// gitignoré) mais elle n'a jamais été validée de bout en bout, et elle a un coût : un secret en
/// clair sur le disque, et un risque NON MESURÉ de conflit de session Rithmic avec le Quantower
/// de l'utilisateur (Rithmic n'autorise en général qu'une session par compte).
/// La voie retenue reste donc : tourner DANS Quantower, où la connexion est déjà vivante et
/// authentifiée. C'est aussi ce qui a fait de `NqTickExtractor` une stratégie.
///
/// Ce qu'il pousse, et pourquoi PAS le reste (mesuré par la sonde le 2026-07-15) :
///   - les TRADES au fil de l'eau (~9/s), avec leur AggressorFlag — couverture mesurée 100 %,
///     donc le footprint côté Python est EXACT, sans la règle du tick héritée d'IBKR ;
///   - des SNAPSHOTS de carnet agrégés par prix, à cadence fixe (~4/s à 250 ms).
/// Le flux L2 brut fait 361 à 472 updates/s : le relayer serait ~100× le débit utile pour rien,
/// puisque `FlowStore.add_book` throttle de toute façon à SNAPSHOT_MS. On sonde donc le carnet
/// agrégé au rythme de l'affichage plutôt que de suivre chaque mouvement d'ordre.
///
/// ⚠ PIÈGE : c'est l'ATTACHE du handler `NewLevel2` qui déclenche l'abonnement L2 (SubscribeAction
/// est interne à la plateforme ; seuls les accesseurs add_/remove_ sont publics). Le handler reste
/// donc attaché même s'il ne fait presque rien — sans lui, `DepthOfMarket` se viderait et les
/// snapshots ne rendraient que du vide.
///
/// ⚠ PIÈGE : ce poste est en locale FRANÇAISE (la sonde journalise « tick=0,25 »). Toute
/// sérialisation passe par InvariantCulture, sinon le JSON sort avec des virgules décimales et le
/// parseur Python le rejette.
/// </summary>
public sealed class NqFeedStrategy : Strategy
{
    // La stratégie sert N'IMPORTE QUEL symbole : c'est ce paramètre qui décide, pas son nom.
    // (Le libellé disait « NQ front », ce qui a fait croire qu'il fallait une stratégie « ES
    // Feed » séparée pour ajouter l'ES — alors que deux instances suffisent, cf. OnRun.)
    [InputParameter("Symbole (NQ, ES, …)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Port TCP (localhost)", 1, 1024, 65535, 1, 0)]
    public int Port = 5555;

    // La sonde a mesuré ~210 niveaux par côté réellement servis. On en demande bien moins : la
    // heatmap n'affiche qu'une bande autour du mid, et chaque niveau est du débit et du dessin.
    [InputParameter("Niveaux de carnet par côté", 2, 1, 500, 1, 0)]
    public int LevelsCount = 100;

    [InputParameter("Cadence des snapshots (ms)", 3, 50, 5000, 50, 0)]
    public int SnapshotMs = 250;

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _snapTimer;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private long _sentTrades, _sentBooks, _dropped;

    public NqFeedStrategy() => Name = "NQ Feed";

    /// <summary>Un client connecté : sa file d'attente et son thread d'écriture.</summary>
    private sealed class Client
    {
        public required TcpClient Tcp;
        public required BlockingCollection<string> Queue;
        public required Guid Id;
    }

    protected override void OnRun()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné (choisir NQ ou ES)."); this.Stop(); return; }

        // ⚠ UN SYMBOLE RÉSOLU N'EST PAS UN SYMBOLE VIVANT — refuser plutôt que servir un mort.
        // Vécu le 2026-07-16 : après un redémarrage de Quantower, la stratégie a démarré AVANT
        // que la connexion Rithmic ne soit rattachée. Le symbole s'est résolu depuis le
        // catalogue local (NQ@CME, l'air correct), mais sans flux derrière. Le pont a alors
        // ouvert son port, envoyé un `hello` avec `tick NaN`, puis servi 4 carnets/s
        // RIGOUREUSEMENT IDENTIQUES et zéro trade — un mort présenté comme un vivant. Côté
        // Python, l'écran affichait « reçu il y a 0,2 s » sur un carnet figé depuis 20 min, et
        // on a cherché le bug dans l'app pendant ce temps.
        //   - `State == Fake`  : la coquille (BusinessObjectState ne vaut que Normal ou Fake).
        //   - `TickSize` NaN   : la signature MESURÉE ce jour-là (Double, cf. réflexion).
        // Les deux, car seule la seconde a été observée en vrai : l'OR ne suppose rien.
        // ⚠ NaN n'est égal à RIEN, pas même à lui-même -> double.IsNaN, jamais `== NaN`.
        if (s.State == BusinessObjectState.Fake || double.IsNaN(s.TickSize) || s.TickSize <= 0)
        {
            this.LogError($"{s.Name} : symbole SANS FLUX (State={s.State}, "
                        + $"TickSize={s.TickSize.ToString(Inv)}) — le pont ne servirait que du vide. "
                        + "Connecter Rithmic dans Quantower, PUIS relancer cette stratégie.");
            this.Stop();
            return;
        }

        _cts = new CancellationTokenSource();

        // IPAddress.Loopback, JAMAIS Any : c'est un flux de données de marché, il n'a aucune raison
        // d'être joignable depuis le réseau.
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            this.LogError($"Impossible d'écouter sur 127.0.0.1:{Port} — {ex.Message} "
                        + "(port déjà pris ? une autre instance de la stratégie tourne ?)");
            this.Stop();
            return;
        }

        // ÉTIQUETTE de l'instance : deux « NQ Feed » (NQ:5555 et ES:5556) sont indiscernables
        // dans le panneau Strategies sans ça.
        // ⚠ `InstanceName` et NON `Name` : `Name` a un setter PROTECTED et sert à dériver
        // `DataFolderName` (le dossier ScriptsData/<nom> (<guid>)/logs). Le toucher
        // éparpillerait les journaux. `InstanceName` a un setter PUBLIC : c'est la propriété
        // prévue pour ça. (Tranché par réflexion sur le BusinessLayer, pas supposé.)
        InstanceName = $"NQ Feed — {s.Name} :{Port.ToString(Inv)}";

        this.LogInfo($"PONT à l'écoute sur 127.0.0.1:{Port} | {s.Name} ({s.Id}) | tick {s.TickSize.ToString(Inv)} "
                   + $"| {LevelsCount} niveaux/côté @ {SnapshotMs} ms");
        this.LogInfo("Côté Python : python -m affichage.quantower_feed --port " + Port.ToString(Inv));

        s.NewLast += OnNewLast;
        s.NewLevel2 += OnNewLevel2;   // ⚠ tient l'abonnement L2 ouvert — ne pas retirer
        s.NewQuote += OnNewQuote;     // ⚠ idem : sans lui, Symbol.Bid/Ask restent à zéro

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        var period = TimeSpan.FromMilliseconds(SnapshotMs);
        _snapTimer = new System.Threading.Timer(_ => PublishBook(), null, period, period);
    }

    protected override void OnStop()
    {
        try { _cts?.Cancel(); } catch { /* déjà annulé */ }
        _snapTimer?.Dispose(); _snapTimer = null;

        if (Instrument is { } s)
        {
            s.NewLast -= OnNewLast;
            s.NewLevel2 -= OnNewLevel2;
            s.NewQuote -= OnNewQuote;
        }

        try { _listener?.Stop(); } catch { /* déjà arrêté */ }
        _listener = null;

        foreach (var c in _clients.Values) DropClient(c);
        _clients.Clear();

        _cts?.Dispose(); _cts = null;
        this.LogInfo($"PONT arrêté. Envoyés : {_sentTrades} trades, {_sentBooks} carnets. Perdus : {_dropped}.");
    }

    // --- Réseau ------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { this.LogError($"Accept : {ex.Message}"); return; }

            tcp.NoDelay = true;   // flux temps réel : ne jamais laisser Nagle bufferiser
            var client = new Client
            {
                Tcp = tcp,
                Id = Guid.NewGuid(),
                // File BORNÉE : un client lent ne doit jamais faire enfler la mémoire de Quantower.
                Queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 10_000),
            };
            _clients[client.Id] = client;
            this.LogInfo($"Client connecté ({_clients.Count} au total).");

            // Le « hello » donne à Python tout ce qu'il faut pour dimensionner ses vues sans
            // rien coder en dur sur l'instrument.
            var s = Instrument!;
            Enqueue(client, "{\"t\":\"hello\""
                + $",\"symbol\":{Json(s.Name)}"
                + $",\"symbol_id\":{Json(s.Id)}"
                + $",\"exchange\":{Json(s.Exchange?.ExchangeName ?? "CME")}"
                + $",\"tick\":{Num(s.TickSize)}"
                + $",\"lot_size\":{Num(s.LotSize)}"
                + $",\"levels\":{LevelsCount.ToString(Inv)}"
                + $",\"snapshot_ms\":{SnapshotMs.ToString(Inv)}"
                + $",\"ts\":{Ms(DateTime.UtcNow).ToString(Inv)}}}");

            _ = Task.Run(() => WriteLoop(client, _cts!.Token));
        }
    }

    /// <summary>Un thread d'écriture PAR client. Indispensable : les événements Quantower arrivent
    /// sur les threads de la plateforme, et écrire la socket directement depuis eux ferait qu'un
    /// client lent ou mort bloquerait le moteur de marché. Ici, les handlers ne font qu'enfiler.</summary>
    private void WriteLoop(Client c, CancellationToken ct)
    {
        try
        {
            using var stream = c.Tcp.GetStream();
            foreach (var line in c.Queue.GetConsumingEnumerable(ct))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (OperationCanceledException) { /* arrêt normal */ }
        catch (Exception ex) { this.LogInfo($"Client déconnecté ({ex.GetType().Name})."); }
        finally
        {
            _clients.TryRemove(c.Id, out _);
            DropClient(c);
        }
    }

    private static void DropClient(Client c)
    {
        try { c.Queue.CompleteAdding(); } catch { }
        try { c.Tcp.Close(); } catch { }
    }

    /// <summary>Enfile sans jamais bloquer : si la file d'un client déborde, on JETTE le message
    /// plutôt que de retarder le flux de marché. Perdre un snapshot est sans conséquence (le
    /// suivant arrive dans 250 ms) ; bloquer un thread Quantower ne l'est pas.</summary>
    private void Enqueue(Client c, string line)
    {
        if (!c.Queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
    }

    private void Broadcast(string line)
    {
        foreach (var c in _clients.Values) Enqueue(c, line);
    }

    // --- Flux --------------------------------------------------------------

    private void OnNewLast(Symbol symbol, Last last)
    {
        if (_clients.IsEmpty) return;

        // AggressorFlag mesuré à 100 % de couverture sur NQ (sonde du 2026-07-15) : on n'infère
        // rien. Les rares trades sans côté sont écartés plutôt que devinés — même politique que
        // NqTickExtractor, qui exclut les 0,0002 % sans agresseur au lieu de leur inventer un sens.
        string? side = last.AggressorFlag switch
        {
            AggressorFlag.Buy => "buy",
            AggressorFlag.Sell => "sell",
            _ => null,
        };
        if (side is null || last.Size <= 0) return;

        Broadcast("{\"t\":\"trade\""
            + $",\"ts\":{Ms(last.Time).ToString(Inv)}"
            + $",\"p\":{Num(last.Price)}"
            + $",\"s\":{Num(last.Size)}"
            + $",\"side\":\"{side}\"}}");
        Interlocked.Increment(ref _sentTrades);
    }

    /// <summary>Ne fait rien d'autre que d'exister : son attache EST l'abonnement L2. Le carnet est
    /// lu par sondage dans PublishBook, à la cadence de l'affichage.</summary>
    private void OnNewLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom) { }

    /// <summary>Idem : son attache EST l'abonnement au flux de cotation, sans lequel `Symbol.Bid`
    /// et `Symbol.Ask` resteraient à zéro. Le top of book est lu dans PublishBook.</summary>
    private void OnNewQuote(Symbol symbol, Quote quote) { }

    private void PublishBook()
    {
        if (_clients.IsEmpty) return;
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
            if (bids.Length == 0 && asks.Length == 0) return;

            var sb = new StringBuilder(256 + 16 * (bids.Length + asks.Length));
            sb.Append("{\"t\":\"book\",\"ts\":").Append(Ms(DateTime.UtcNow).ToString(Inv));
            // Le TOP OF BOOK du flux de COTATION (`NewQuote`), chemin indépendant de l'agrégation
            // du carnet ci-dessus. Deux usages : les lignes best bid/ask de la vue, et un
            // contre-contrôle permanent — si `quote_bid`/`quote_ask` s'écartent du 1er niveau de
            // `b`/`a`, c'est que l'agrégation du DOM perd des niveaux.
            sb.Append(",\"qb\":").Append(Num(s.Bid)).Append(",\"qa\":").Append(Num(s.Ask));
            sb.Append(",\"qbs\":").Append(Num(s.BidSize)).Append(",\"qas\":").Append(Num(s.AskSize));
            AppendLevels(sb, ",\"b\":", bids);
            AppendLevels(sb, ",\"a\":", asks);
            sb.Append('}');
            Broadcast(sb.ToString());
            Interlocked.Increment(ref _sentBooks);
        }
        catch (Exception ex) { this.LogError($"Snapshot carnet : {ex.Message}"); }
    }

    private static void AppendLevels(StringBuilder sb, string key, Level2Item[] levels)
    {
        sb.Append(key).Append('[');
        bool first = true;
        foreach (var lv in levels)
        {
            if (lv is null || lv.Size <= 0) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('[').Append(Num(lv.Price)).Append(',').Append(Num(lv.Size)).Append(']');
        }
        sb.Append(']');
    }

    // --- Sérialisation -----------------------------------------------------

    /// <summary>Nombre en JSON. InvariantCulture OBLIGATOIRE : ce poste est en locale française,
    /// où le formatage par défaut rend « 0,25 » — du JSON invalide.</summary>
    private static string Num(double v) => v.ToString("R", Inv);

    private static string Json(string? s)
    {
        if (s is null) return "null";
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch < ' ' ? "\\u" + ((int)ch).ToString("x4", Inv) : ch.ToString(),
            });
        return sb.Append('"').ToString();
    }

    private static long Ms(DateTime t) => (long)(t.ToUniversalTime() - Epoch).TotalMilliseconds;
}
