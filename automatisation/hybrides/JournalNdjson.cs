using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Hybrides;

/// <summary>
/// Journal de décisions NDJSON — le MÊME format que les jumeaux LEAN du volet C
/// (backtesting/backtests/algorithms/cadre_hybride.py) : un fichier par jour ET et par
/// stratégie, une ligne par événement. C'est CE fichier que la phase 4 (shadow) comparera
/// au journal du jumeau.
///
/// Champs : ts (UTC ISO), strategie, symbole, evenement, prix, qte, id_ordre, raison,
/// indicateurs{}. Vocabulaire des specs : signal|entree_envoyee|fill|bracket_pose|
/// stop_modifie|sortie_envoyee|annulation|garde_fou|flat_force|kill — PLUS demarrage|arret
/// (ajout live : le critère de succès « redémarrage propre le lendemain » doit se lire ici).
///
/// Patron du pont NqFeed : les threads de marché ne font qu'ENFILER (file bornée), un seul
/// thread écrit le disque — jamais d'I/O sur un thread Quantower. Si la file déborde, on
/// jette (compteur) plutôt que de bloquer le moteur.
/// ⚠ InvariantCulture PARTOUT (piège 6 du REPRISE : locale française → « 0,25 » = JSON mort).
/// Fichiers en APPEND : un redémarrage en cours de journée COMPLÈTE le fichier du jour
/// (le jumeau LEAN, lui, réécrit à chaque run — asymétrie assumée et documentée).
/// </summary>
internal sealed class JournalNdjson : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo Et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private readonly string _strategie;
    private readonly string _symbole;
    private readonly string _dossier;
    private readonly BlockingCollection<string> _file = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _ecrivain;
    private readonly Action<string> _logErreur;
    private long _jetes;

    public string Dossier => _dossier;
    public long LignesJetees => Interlocked.Read(ref _jetes);

    public JournalNdjson(string dossierBase, string strategie, string symbole, Action<string> logErreur)
    {
        _strategie = strategie;
        _symbole = symbole;
        _logErreur = logErreur;
        _dossier = Path.Combine(dossierBase, strategie);
        Directory.CreateDirectory(_dossier);
        _ecrivain = new Thread(BoucleEcriture) { IsBackground = true, Name = $"journal-{strategie}" };
        _ecrivain.Start();
    }

    /// <summary>Enfile un événement. Ne bloque JAMAIS le thread appelant.</summary>
    public void Ecrire(DateTime tsUtc, string evenement, double? prix = null, double? qte = null,
                       string? idOrdre = null, string raison = "",
                       params (string cle, double valeur)[] indicateurs)
    {
        var t = tsUtc.Kind == DateTimeKind.Utc ? tsUtc : tsUtc.ToUniversalTime();
        var sb = new StringBuilder(256);
        sb.Append("{\"ts\":\"").Append(t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+00:00'", Inv)).Append('"')
          .Append(",\"strategie\":").Append(Json(_strategie))
          .Append(",\"symbole\":").Append(Json(_symbole))
          .Append(",\"evenement\":").Append(Json(evenement))
          .Append(",\"prix\":").Append(prix is null ? "null" : Num(prix.Value))
          .Append(",\"qte\":").Append(qte is null ? "null" : Num(qte.Value))
          .Append(",\"id_ordre\":").Append(Json(idOrdre))
          .Append(",\"raison\":").Append(Json(raison))
          .Append(",\"indicateurs\":{");
        bool premier = true;
        foreach (var (cle, valeur) in indicateurs)
        {
            if (double.IsNaN(valeur)) continue;
            if (!premier) sb.Append(',');
            premier = false;
            sb.Append(Json(cle)).Append(':').Append(Num(valeur));
        }
        sb.Append("}}");
        if (!_file.TryAdd(sb.ToString())) Interlocked.Increment(ref _jetes);
    }

    /// <summary>Le SEUL endroit qui touche le disque. Un fichier par jour ET, ouvert en
    /// append, basculé quand la date ET de l'événement change.</summary>
    private void BoucleEcriture()
    {
        StreamWriter? fichier = null;
        DateTime jourCourant = DateTime.MinValue;
        try
        {
            foreach (var ligne in _file.GetConsumingEnumerable())
            {
                try
                {
                    // La date ET se relit dans le ts de la ligne (25 premiers caractères ISO).
                    var tsUtc = DateTime.ParseExact(ligne.Substring(7, 23), "yyyy-MM-dd'T'HH:mm:ss.fff",
                                                    Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    var jourEt = TimeZoneInfo.ConvertTimeFromUtc(tsUtc, Et).Date;
                    if (jourEt != jourCourant)
                    {
                        fichier?.Dispose();
                        jourCourant = jourEt;
                        var chemin = Path.Combine(_dossier, jourEt.ToString("yyyy-MM-dd", Inv) + ".ndjson");
                        fichier = new StreamWriter(chemin, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    fichier!.WriteLine(ligne);
                }
                catch (Exception ex) { _logErreur($"Journal NDJSON : {ex.Message}"); }
            }
        }
        finally { fichier?.Dispose(); }
    }

    public void Dispose()
    {
        try { _file.CompleteAdding(); } catch { /* déjà complété */ }
        if (!_ecrivain.Join(TimeSpan.FromSeconds(5)))
            _logErreur("Journal NDJSON : thread d'écriture non terminé après 5 s.");
    }

    // --- Sérialisation (patron NqFeed : jamais le formatage de la locale) ---------------

    private static string Num(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, 2).ToString("0.##", Inv);

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
}
