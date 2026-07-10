using System.Drawing;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace RsiNq;

/// <summary>
/// Phase 3 — indicateur RSI 14 (NATIF Quantower) sur le graphe NQ, avec export de parité.
/// Contrairement à la SMA (formule sans ambiguïté), le RSI a un LISSAGE (Wilder/SMMA vs
/// simple) et un SEED de warmup : c'est le cas de parité intéressant — on ATTEND un écart
/// entre le RSI natif Quantower et une implémentation Python, à quantifier et expliquer.
///
/// On héberge le RSI natif comme sous-indicateur (Core.Instance.Indicators.BuiltIn.RSI) —
/// on ne réécrit PAS le RSI (consigne du cahier des charges). Export : time,close,rsi → comparé côté
/// Python à DEUX références (Wilder SMMA et Cutler/SMA) pour identifier le lissage utilisé.
/// </summary>
public sealed class RsiNqIndicator : Indicator
{
    [InputParameter("Période RSI", 0, 2, 1000, 1, 0)]
    public int Periode = 14;

    [InputParameter("Exporter la parité (CSV)", 1)]
    public bool ExportParite = true;

    [InputParameter("Fichier parité", 2)]
    public string FichierParite = @"F:\data\parity\NQ-rsi-quantower.csv";

    private Indicator? _rsi;             // RSI natif hébergé
    private DateTime _derniereBarre;

    public RsiNqIndicator()
    {
        Name = "RSI NQ (14, natif)";
        SeparateWindow = true;                               // oscillateur : fenêtre séparée
        AddLineSeries("RSI", Color.MediumPurple, 2, LineStyle.Solid);
    }

    protected override void OnInit()
    {
        _derniereBarre = default;
        // RSI natif : RSIMode.Exponential + MaMode.SMMA = lissage de Wilder (le "RSI 14" classique).
        _rsi = Core.Instance.Indicators.BuiltIn.RSI(
            Periode, PriceType.Close, RSIMode.Exponential, MaMode.SMMA, Periode,
            IndicatorCalculationType.AllAvailableData);
        AddIndicator(_rsi);

        AddLineLevel(70, "surachat", Color.Gray, 1, LineStyle.Dash);
        AddLineLevel(30, "survente", Color.Gray, 1, LineStyle.Dash);

        if (ExportParite)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FichierParite)!);
                File.WriteAllText(FichierParite, "time,close,rsi\n");
            }
            catch { /* disque indisponible : on n'empêche pas l'affichage */ }
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (_rsi is null || Periode >= Count) return;        // warmup
        double rsi = _rsi.GetValue();                        // valeur du RSI natif, barre courante
        SetValue(rsi);                                       // ligne 0

        if (ExportParite)
        {
            DateTime t = this.Time(0);
            if (t != _derniereBarre)
            {
                _derniereBarre = t;
                try
                {
                    File.AppendAllText(FichierParite, string.Format(CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2}\n",
                        t, this.GetPrice(PriceType.Close, 0), rsi));
                }
                catch { /* ignore */ }
            }
        }
    }
}
