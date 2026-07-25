using System.Globalization;
using System.Text;

namespace Hybrides;

/// <summary>Source des trades dessinés par un visuel : SIMULATION (auto — tous les croisements
/// joués) ou JOURNAL RÉEL (mode confirmation — seulement les trades confirmés sur le compte).</summary>
public enum ModeSource { Simulation, Reel }

/// <summary>Style d'un trait paramétrable dans les visuels.</summary>
public enum StyleTrait { Plein, Tirets, Points }

/// <summary>
/// Lecteur du journal NDJSON RÉEL d'une stratégie hybride — reconstruit la liste des trades
/// RÉELLEMENT pris (mode confirmation), pour que le visuel dessine ce qui s'est passé sur le
/// compte plutôt que le scénario « auto » simulé (tous les croisements joués).
///
/// Décorrélé de la stratégie : il lit le fichier <base>\<slug>\*.ndjson le plus récent — le
/// MÊME que le terminal `suivre-journal.ps1`. Machine à états sur les événements :
///   fill (entrée : sens via qte signé) → bracket_pose (SL/TP) → stop_modifie* (l'escalier
///   réel) → sortie_envoyee / annulation / fill (sortie + type).
/// Relu au plus une fois par intervalle, et seulement si le fichier a grossi (throttle : reste
/// temps réel sans marteler le disque à chaque repaint).
///
/// Extraction JSON MANUELLE (comme <see cref="JournalNdjson"/> écrit à la main) — pas de
/// dépendance System.Text.Json dans le plugin. Le format est contrôlé (on l'écrit nous-mêmes).
/// InvariantCulture partout (piège 6 du REPRISE : locale FR).
/// </summary>
public sealed class LecteurJournalTrades
{
    /// <summary>Un trade reconstruit depuis le journal. Superset des besoins des 3 visuels
    /// (H1/H3 utilisent Sl/Tp ; H2 utilise Trail).</summary>
    public sealed class TradeReel
    {
        public DateTime EntreeTemps;
        public double EntreePrix;
        public int Sens;                          // +1 long, -1 short
        public double Sl = double.NaN, Tp = double.NaN, StopInitial = double.NaN;
        public readonly List<(DateTime t, double stop)> Trail = new();   // escalier du suiveur (H2)
        public DateTime? SortieTemps;
        public double SortieNiveau;
        public char SortieType;                   // 'S' SL · 'T' TP · 'X' annulation/signal · 'F' flat · 'K' kill
        public double Pts, R;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _base;
    private readonly string _slug;
    private readonly double _tick;
    private readonly TimeSpan _intervalle;

    private readonly object _lock = new();
    private List<TradeReel> _trades = new();
    private DateTime _prochaineLecture = DateTime.MinValue;
    private string? _fichier;
    private long _tailleLue = -1;

    public LecteurJournalTrades(string dossierBase, string slug, double tick, TimeSpan? intervalle = null)
    {
        _base = dossierBase;
        _slug = slug;
        _tick = tick > 0 ? tick : 0.25;
        _intervalle = intervalle ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Copie thread-safe des trades. Relit le fichier si l'intervalle est écoulé ET
    /// que le fichier a changé (nom ou taille). Jamais d'exception vers l'appelant (repaint).</summary>
    public TradeReel[] Trades(DateTime maintenantUtc)
    {
        if (maintenantUtc >= _prochaineLecture)
        {
            _prochaineLecture = maintenantUtc + _intervalle;
            try { Relire(); } catch { /* fichier absent, verrou, ligne corrompue : on garde l'état */ }
        }
        lock (_lock) return _trades.ToArray();
    }

    private void Relire()
    {
        var dir = Path.Combine(_base, _slug);
        if (!Directory.Exists(dir)) return;
        var f = new DirectoryInfo(dir).GetFiles("*.ndjson")
            .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
        if (f is null) return;
        if (f.FullName == _fichier && f.Length == _tailleLue) return;   // rien de neuf
        _fichier = f.FullName;
        _tailleLue = f.Length;

        var trades = new List<TradeReel>();
        TradeReel? cur = null;
        char sortieAttendue = '\0';

        using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? ligne;
        while ((ligne = sr.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(ligne)) continue;
            string ev = Champ(ligne, "evenement") ?? "";
            string raison = Champ(ligne, "raison") ?? "";

            switch (ev)
            {
                case "fill":
                    if (Contient(raison, "entr"))          // « fill d'entrée » (réel ou simulé)
                    {
                        double qte = Num(Champ(ligne, "qte"));
                        cur = new TradeReel
                        {
                            EntreeTemps = TsUtc(ligne),
                            EntreePrix = Num(Champ(ligne, "prix")),
                            Sens = qte > 0 ? 1 : qte < 0 ? -1 : 0,
                        };
                        trades.Add(cur);
                        sortieAttendue = '\0';
                    }
                    else if (cur is not null)              // fill de clôture
                    {
                        double niv = Num(Champ(ligne, "prix"));
                        Fermer(cur, TsUtc(ligne), niv, TypeSortie(raison, sortieAttendue, cur, niv));
                        cur = null; sortieAttendue = '\0';
                    }
                    break;

                case "bracket_pose":
                    if (cur is not null)
                    {
                        double stop = Num(Champ(ligne, "stop"));
                        double take = Num(Champ(ligne, "take"));
                        double px = Num(Champ(ligne, "prix"));
                        if (px > 0) cur.EntreePrix = px;   // prix d'ouverture de la position
                        if (!double.IsNaN(stop))
                        {
                            cur.Sl = cur.StopInitial = stop;
                            if (cur.Trail.Count == 0) cur.Trail.Add((cur.EntreeTemps, stop));
                        }
                        if (!double.IsNaN(take)) cur.Tp = take;
                        if (cur.Sens == 0 && !double.IsNaN(stop) && px > 0)
                            cur.Sens = stop < px ? 1 : -1;  // repli : SL sous le prix ⇒ long
                    }
                    break;

                case "stop_modifie":
                    if (cur is not null)
                    {
                        double s = Num(Champ(ligne, "prix"));
                        if (!double.IsNaN(s)) { cur.Sl = s; cur.Trail.Add((TsUtc(ligne), s)); }
                    }
                    break;

                case "sortie_envoyee":
                    sortieAttendue = TypeDeRaison(raison);
                    break;

                case "annulation":
                    if (Contient(raison, "annul")) sortieAttendue = 'X';
                    break;

                case "flat_force":
                    if (cur is not null) { Fermer(cur, TsUtc(ligne), Num(Champ(ligne, "prix")), 'F'); cur = null; }
                    break;

                case "kill":
                    if (cur is not null)
                    {
                        double niv = Num(Champ(ligne, "prix"));
                        Fermer(cur, TsUtc(ligne), double.IsNaN(niv) ? cur.EntreePrix : niv, 'K');
                        cur = null;
                    }
                    break;
            }
        }

        lock (_lock) _trades = trades;
    }

    private void Fermer(TradeReel t, DateTime ts, double niveau, char type)
    {
        if (double.IsNaN(niveau)) niveau = t.EntreePrix;
        t.SortieTemps = ts;
        t.SortieNiveau = niveau;
        t.SortieType = type;
        t.Pts = (niveau - t.EntreePrix) * t.Sens;
        double baseStop = double.IsNaN(t.StopInitial) ? t.Sl : t.StopInitial;
        double risque = Math.Abs(t.EntreePrix - baseStop);
        t.R = risque > 0 ? t.Pts / risque : 0;
    }

    /// <summary>Type de sortie du fill de clôture : d'abord les marqueurs explicites du fill,
    /// puis l'intention annoncée (sortie_envoyee/annulation), enfin le niveau (proximité SL/TP).</summary>
    private char TypeSortie(string fillRaison, char attendue, TradeReel t, double niveau)
    {
        if (Contient(fillRaison, "[SL]")) return 'S';
        if (Contient(fillRaison, "[TP]")) return 'T';
        if (Contient(fillRaison, "crois")) return 'X';
        if (Contient(fillRaison, "flat") || Contient(fillRaison, "FLAT")) return 'F';
        if (attendue != '\0') return attendue;
        double seuil = 4 * _tick;
        if (!double.IsNaN(t.Sl) && Math.Abs(niveau - t.Sl) <= seuil) return 'S';
        if (!double.IsNaN(t.Tp) && Math.Abs(niveau - t.Tp) <= seuil) return 'T';
        return 'X';
    }

    private static char TypeDeRaison(string raison)
    {
        if (Contient(raison, "crois")) return 'X';
        if (Contient(raison, "flat")) return 'F';
        if (Contient(raison, "SL")) return 'S';
        if (Contient(raison, "TP")) return 'T';
        return 'X';
    }

    // ── Extraction JSON manuelle (format plat contrôlé) ───────────────────────────────
    private static bool Contient(string s, string sub) =>
        !string.IsNullOrEmpty(s) && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

    private static double Num(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : double.NaN;

    private DateTime TsUtc(string ligne)
    {
        var s = Champ(ligne, "ts");
        return s is not null && DateTime.TryParse(s, Inv,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : DateTime.MinValue;
    }

    /// <summary>Renvoie la valeur d'une clé (chaîne déséchappée, ou nombre en texte). Les clés
    /// stop/take/prix/qte sont uniques dans la ligne (stop/take vivent dans indicateurs{}, mais
    /// aucune autre clé ne s'appelle ainsi). null si absente ou JSON null.</summary>
    private static string? Champ(string ligne, string cle)
    {
        string pat = "\"" + cle + "\":";
        int i = ligne.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return null;
        i += pat.Length;
        if (i >= ligne.Length) return null;
        if (ligne[i] == '"')
        {
            var sb = new StringBuilder();
            int j = i + 1;
            while (j < ligne.Length && ligne[j] != '"')
            {
                if (ligne[j] == '\\' && j + 1 < ligne.Length)
                {
                    j++;
                    sb.Append(ligne[j] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var c => c });
                }
                else sb.Append(ligne[j]);
                j++;
            }
            return sb.ToString();
        }
        int k = i;
        while (k < ligne.Length && ligne[k] != ',' && ligne[k] != '}') k++;
        var val = ligne.Substring(i, k - i).Trim();
        return val == "null" || val.Length == 0 ? null : val;
    }
}
