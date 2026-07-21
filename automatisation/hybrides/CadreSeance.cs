using System.Globalization;

namespace Hybrides;

/// <summary>
/// Cadre de séance COMMUN des 3 hybrides — le miroir C# de cadre_hybride.py (jumeaux LEAN,
/// volet C) : heures en ET, fenêtre d'entrée, garde-fou journalier (pertes pleines) ré-armé
/// à l'ouverture, cooldown après sortie. Le live et le jumeau vivent sous les MÊMES règles,
/// sinon la parité de la phase 4 est fausse d'avance.
/// ⚠ Heure d'été : conversions par TimeZoneInfo, jamais d'offset en dur.
/// 🪤 Clôtures avancées (trouvaille du volet C) : les jours de séance écourtée (ex. 3 juillet,
/// CME 13:00 ET), la barre de 16:55 n'existe pas — l'heure de flat est donc un PARAMÈTRE de
/// stratégie, à avancer ces jours-là (voir « décisions restantes » des specs).
/// </summary>
public sealed class CadreSeance
{
    private static readonly TimeZoneInfo Et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public int EntreeDebut { get; }   // minutes ET depuis minuit (défaut 09:30 = 570)
    public int EntreeFin { get; }     // 15:30 = 930 — plus rien de neuf après
    public int FlatForce { get; }     // 16:55 = 1015 — tout annuler + liquider
    public int PertesMax { get; }
    public int CooldownMin { get; }

    public bool GardeFou { get; private set; }
    public int PertesDuJour { get; private set; }

    private DateTime _sortieUtc = DateTime.MinValue;
    private DateTime _jourArme = DateTime.MinValue;

    public CadreSeance(string entreeDebutEt, string entreeFinEt, string flatEt, int pertesMax, int cooldownMin)
    {
        EntreeDebut = ParseHeure(entreeDebutEt);
        EntreeFin = ParseHeure(entreeFinEt);
        FlatForce = ParseHeure(flatEt);
        PertesMax = pertesMax;
        CooldownMin = cooldownMin;
    }

    /// <summary>« HH:mm » → minutes depuis minuit. Lève si le format est invalide (on refuse
    /// de démarrer avec une heure de flat illisible plutôt que de deviner).</summary>
    public static int ParseHeure(string hhmm)
    {
        var parts = hhmm.Trim().Split(':');
        if (parts.Length != 2) throw new FormatException($"Heure invalide « {hhmm} » (attendu HH:mm).");
        int h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int m = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (h is < 0 or > 23 || m is < 0 or > 59)
            throw new FormatException($"Heure invalide « {hhmm} » (attendu HH:mm).");
        return h * 60 + m;
    }

    /// <summary>Instant UTC → (date ET, minutes ET depuis minuit).</summary>
    public static (DateTime JourEt, int Minutes) HeureEt(DateTime utc)
    {
        var t = TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime(), Et);
        return (t.Date, t.Hour * 60 + t.Minute);
    }

    /// <summary>À appeler à chaque barre/tic d'horloge : au premier passage de l'ouverture
    /// d'un nouveau jour ET, remise à zéro (le garde-fou arrête « jusqu'au prochain 09:30 »).</summary>
    public void MajJour(DateTime jourEt, int minutes)
    {
        if (minutes >= EntreeDebut && jourEt != _jourArme)
        {
            _jourArme = jourEt;
            PertesDuJour = 0;
            GardeFou = false;
        }
    }

    public bool CooldownOk(DateTime utc) =>
        _sortieUtc == DateTime.MinValue || (utc - _sortieUtc).TotalSeconds >= CooldownMin * 60;

    /// <summary>Enregistre une sortie ; renvoie true si le garde-fou VIENT de se déclencher.</summary>
    public bool Sortie(DateTime utc, bool pertePleine)
    {
        _sortieUtc = utc;
        if (!pertePleine) return false;
        PertesDuJour++;
        // PertesMax <= 0 = garde-fou DÉSACTIVÉ (phase de test : on veut voir des signaux, pas
        // arrêter la stratégie après 2 scalps perdants). Le remettre à 2 pour tester CE
        // mécanisme précis.
        if (PertesMax > 0 && PertesDuJour >= PertesMax && !GardeFou)
        {
            GardeFou = true;
            return true;
        }
        return false;
    }
}
