namespace Hybrides;

/// <summary>
/// Indicateurs maison des 3 hybrides — les MÊMES formules que les jumeaux LEAN du volet C
/// (SMA simple, RSI et ATR en lissage de Wilder), plutôt que les indicateurs Quantower :
/// la parité de la phase 4 se joue sur les décisions, donc sur les valeurs d'indicateurs.
/// Amorçage à la LEAN : moyenne simple des N premières valeurs, puis récurrence de Wilder
/// (les écarts d'amorçage s'éteignent d'eux-mêmes — le seed de 48 h est très au-delà).
/// </summary>
internal sealed class Sma
{
    private readonly int _n;
    private readonly Queue<double> _valeurs = new();
    private double _somme;

    public Sma(int n) => _n = n;
    public bool Prete => _valeurs.Count >= _n;
    public double Valeur { get; private set; } = double.NaN;

    public void Ajouter(double close)
    {
        _valeurs.Enqueue(close);
        _somme += close;
        if (_valeurs.Count > _n) _somme -= _valeurs.Dequeue();
        if (Prete) Valeur = _somme / _n;
    }
}

/// <summary>RSI de Wilder (le RelativeStrengthIndex(WILDERS) de LEAN).</summary>
internal sealed class RsiWilder
{
    private readonly int _n;
    private double _closePrec = double.NaN;
    private double _gainMoyen, _perteMoyenne;
    private int _nb;

    public RsiWilder(int n) => _n = n;
    public bool Pret => _nb >= _n;
    public double Valeur { get; private set; } = double.NaN;

    public void Ajouter(double close)
    {
        if (double.IsNaN(_closePrec)) { _closePrec = close; return; }
        double delta = close - _closePrec;
        _closePrec = close;
        double gain = delta > 0 ? delta : 0;
        double perte = delta < 0 ? -delta : 0;
        _nb++;
        if (_nb <= _n)
        {
            // amorçage : moyenne simple des N premiers deltas
            _gainMoyen += (gain - _gainMoyen) / _nb;
            _perteMoyenne += (perte - _perteMoyenne) / _nb;
        }
        else
        {
            _gainMoyen = (_gainMoyen * (_n - 1) + gain) / _n;
            _perteMoyenne = (_perteMoyenne * (_n - 1) + perte) / _n;
        }
        if (!Pret) return;
        Valeur = _perteMoyenne <= 0 ? 100.0 : 100.0 - 100.0 / (1.0 + _gainMoyen / _perteMoyenne);
    }
}

/// <summary>ATR de Wilder (l'AverageTrueRange(WILDERS) de LEAN). Premier TR = high−low.</summary>
internal sealed class AtrWilder
{
    private readonly int _n;
    private double _closePrec = double.NaN;
    private int _nb;

    public AtrWilder(int n) => _n = n;
    public bool Pret => _nb >= _n;
    public double Valeur { get; private set; } = double.NaN;

    public void Ajouter(double haut, double bas, double close)
    {
        double tr = double.IsNaN(_closePrec)
            ? haut - bas
            : Math.Max(haut - bas, Math.Max(Math.Abs(haut - _closePrec), Math.Abs(bas - _closePrec)));
        _closePrec = close;
        _nb++;
        Valeur = _nb <= _n
            ? (double.IsNaN(Valeur) ? tr : Valeur + (tr - Valeur) / _nb)   // amorçage : moyenne simple
            : (Valeur * (_n - 1) + tr) / _n;                               // récurrence de Wilder
    }
}

/// <summary>Une barre 1 m fermée (temps d'OUVERTURE en UTC, aligné à la minute). Public :
/// c'est le paramètre du SurBarre1m protégé de la base (accessibilité oblige).</summary>
public readonly record struct Barre1m(DateTime OuvertureUtc, double Open, double High, double Low, double Close)
{
    public DateTime FinUtc => OuvertureUtc.AddMinutes(1);
}

/// <summary>
/// Agrège les barres 1 m en barres de TF minutes — la MÊME règle que les jumeaux LEAN :
/// on accumule, et la barre TF ferme quand la minute de FIN de la barre 1 m est un multiple
/// de TF (minute UTC ; l'ET n'en diffère que d'heures entières, le modulo 3/5 est identique).
/// </summary>
internal sealed class AggregateurBarres
{
    private readonly int _tfMin;
    private double _o = double.NaN, _h, _l;

    public AggregateurBarres(int tfMin) => _tfMin = tfMin;

    /// <summary>Ajoute une barre 1 m fermée ; renvoie la barre TF si elle vient de fermer.</summary>
    public (double O, double H, double L, double C, DateTime FinUtc)? Ajouter(in Barre1m b)
    {
        if (double.IsNaN(_o)) { _o = b.Open; _h = b.High; _l = b.Low; }
        else { _h = Math.Max(_h, b.High); _l = Math.Min(_l, b.Low); }
        if (b.FinUtc.Minute % _tfMin != 0) return null;
        var barre = (_o, _h, _l, b.Close, b.FinUtc);
        _o = double.NaN;
        return barre;
    }
}
