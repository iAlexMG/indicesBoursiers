using System.Drawing;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace SignauxNq;

/// <summary>
/// Phase 3 — Indicateur « signaux » : reproduit SUR LE GRAPHE la logique de la stratégie n°7
/// (`strategie_avancee_nq`) et pose les marqueurs entrée/sortie. Pont vers la Phase 4 (shadow
/// mode : même logique, journalisée en live sans ordre).
///   Régime  : close > SMA 200 (ligne orange = repère de régime).
///   Entrée  : croisement MACD(12,26,9) au-dessus de son signal + RSI(14) > 50, régime haussier.
///   Sorties : stop suiveur 2×ATR sous le plus haut close, take = entrée + 4×ATR, ou casse de régime.
/// Indicateurs NATIFS réutilisés (SMA/EMA/RSI/ATR) ; MACD = EMA12−EMA26, signal = EMA9 du MACD.
/// À utiliser sur un graphe **NQ 1 h** (résolution de la stratégie). Export des signaux → future
/// concordance avec le backtest (Phase 4). Long/flat.
/// </summary>
public sealed class SignauxNqIndicator : Indicator
{
    [InputParameter("Exporter les signaux (CSV)", 0)]
    public bool ExportSignaux = true;

    [InputParameter("Fichier signaux", 1)]
    public string FichierSignaux = @"H:\IndicesBoursiers\parity\NQ-signaux-quantower.csv";

    private const int PSma = 200, PRsi = 14, PAtr = 14, SeuilRsi = 50;
    private const double StopMult = 2.0, TakeMult = 4.0;

    private Indicator? _sma, _rsi, _atr, _ema12, _ema26;
    private double _signalEma; private bool _signalSeeded;
    private double? _diffPrec;
    private bool _investi;
    private double _entry, _stop, _take, _plusHaut;
    private string _raison = "";

    public SignauxNqIndicator()
    {
        Name = "Signaux NQ (strat. avancée)";
        SeparateWindow = false;
        AddLineSeries("SMA 200 (régime)", Color.Orange, 1, LineStyle.Solid);
        AddLineSeries("signaux", Color.FromArgb(0, 0, 0, 0), 1, LineStyle.Solid); // invisible : porte les marqueurs
    }

    protected override void OnInit()
    {
        _signalSeeded = false; _diffPrec = null; _investi = false; _raison = "";
        var calc = IndicatorCalculationType.AllAvailableData;
        _sma = Core.Instance.Indicators.BuiltIn.SMA(PSma, PriceType.Close);
        _rsi = Core.Instance.Indicators.BuiltIn.RSI(PRsi, PriceType.Close, RSIMode.Exponential, MaMode.SMMA, PRsi, calc);
        _atr = Core.Instance.Indicators.BuiltIn.ATR(PAtr, MaMode.SMMA, calc);
        _ema12 = Core.Instance.Indicators.BuiltIn.EMA(12, PriceType.Close, calc);
        _ema26 = Core.Instance.Indicators.BuiltIn.EMA(26, PriceType.Close, calc);
        foreach (var ind in new[] { _sma, _rsi, _atr, _ema12, _ema26 }) AddIndicator(ind);

        if (ExportSignaux)
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FichierSignaux)!);
                System.IO.File.WriteAllText(FichierSignaux, "time,action,raison,prix,rsi,regime\n");
            }
            catch { }
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (_sma is null || Count <= PSma + 26) return;      // warmup (SMA200 + MACD)

        double close = this.GetPrice(PriceType.Close, 0);
        double sma = _sma.GetValue();
        SetValue(sma, 0);                                    // ligne régime
        SetValue(close, 1);                                  // ligne invisible (porte les marqueurs)

        double rsi = _rsi!.GetValue();
        double atr = _atr!.GetValue();
        double macd = _ema12!.GetValue() - _ema26!.GetValue();
        _signalEma = _signalSeeded ? 0.2 * macd + 0.8 * _signalEma : macd;
        _signalSeeded = true;
        double diff = macd - _signalEma;
        bool croiseHaut = _diffPrec is double dp && dp <= 0 && diff > 0;
        _diffPrec = diff;
        bool regime = close > sma;

        if (_investi)
        {
            _plusHaut = Math.Max(_plusHaut, close);
            double nouveauStop = _plusHaut - StopMult * atr;
            if (nouveauStop > _stop) _stop = nouveauStop;
            _raison = close <= _stop ? "STOP" : close >= _take ? "TAKE" : !regime ? "REGIME" : "";
            if (_raison.Length > 0)
            {
                _investi = false;
                PoseMarqueur(false, _raison);
                Exporter(close, "VENTE", _raison, rsi, regime);
            }
        }
        else if (regime && croiseHaut && rsi > SeuilRsi)
        {
            _investi = true;
            _entry = close; _plusHaut = close;
            _stop = close - StopMult * atr; _take = close + TakeMult * atr;
            PoseMarqueur(true, "ENTREE");
            Exporter(close, "ACHAT", "ENTREE", rsi, regime);
        }
    }

    private void PoseMarqueur(bool entree, string raison)
    {
        Color c = entree ? Color.LimeGreen
            : raison == "TAKE" ? Color.LimeGreen : raison == "REGIME" ? Color.Orange : Color.Red;
        LinesSeries[1].SetMarker(0, new IndicatorLineMarker
        {
            Color = c,
            UpperIcon = entree ? IndicatorLineMarkerIconType.None : IndicatorLineMarkerIconType.DownArrow,
            BottomIcon = entree ? IndicatorLineMarkerIconType.UpArrow : IndicatorLineMarkerIconType.None,
        });
    }

    private void Exporter(double prix, string action, string raison, double rsi, bool regime)
    {
        if (!ExportSignaux) return;
        try
        {
            System.IO.File.AppendAllText(FichierSignaux, string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2},{3},{4:F1},{5}\n",
                this.Time(0), action, raison, prix, rsi, regime ? 1 : 0));
        }
        catch { }
    }
}
