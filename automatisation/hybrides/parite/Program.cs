// Parité indicateurs C# (Hybrides) vs LEAN (jumeaux volet C) : rejoue le CSV 1 m canonique
// dans les classes de Indicateurs.cs et imprime les valeurs aux bornes 3 m / 5 m.
// Sortie : ts_fin_utc;sma9_5m;sma21_5m;atr14_5m;rsi9_3m;atr14_3m  (InvariantCulture)
using System.Globalization;
using Hybrides;

var inv = CultureInfo.InvariantCulture;
string csv = args.Length > 0 ? args[0] : @"H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09\1m.csv";
var de = DateTime.Parse(args.Length > 1 ? args[1] : "2026-06-01", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
var a = DateTime.Parse(args.Length > 2 ? args[2] : "2026-07-10", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

var agg5 = new AggregateurBarres(5);
var agg3 = new AggregateurBarres(3);
var sma9 = new Sma(9);
var sma21 = new Sma(21);
var atr5 = new AtrWilder(14);
var rsi3 = new RsiWilder(9);
var atr3 = new AtrWilder(14);

using var sortie = new StreamWriter(args.Length > 3 ? args[3] : "parite_csharp.csv");
sortie.WriteLine("ts;sma9;sma21;atr5;rsi3;atr3");
foreach (var ligne in File.ReadLines(csv))
{
    if (ligne.Length == 0 || !char.IsDigit(ligne[0])) continue;
    var c = ligne.Split(',');
    var ouverture = DateTime.ParseExact(c[0][..19], "yyyy-MM-dd HH:mm:ss", inv,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    if (ouverture < de) continue;
    if (ouverture >= a) break;
    var b = new Barre1m(ouverture,
                        double.Parse(c[1], inv), double.Parse(c[2], inv),
                        double.Parse(c[3], inv), double.Parse(c[4], inv));
    string s9 = "", s21 = "", a5 = "", r3 = "", a3 = "";
    if (agg5.Ajouter(b) is { } b5)
    {
        sma9.Ajouter(b5.C); sma21.Ajouter(b5.C); atr5.Ajouter(b5.H, b5.L, b5.C);
        if (sma9.Prete) s9 = sma9.Valeur.ToString("F4", inv);
        if (sma21.Prete) s21 = sma21.Valeur.ToString("F4", inv);
        if (atr5.Pret) a5 = atr5.Valeur.ToString("F4", inv);
    }
    if (agg3.Ajouter(b) is { } b3)
    {
        rsi3.Ajouter(b3.C); atr3.Ajouter(b3.H, b3.L, b3.C);
        if (rsi3.Pret) r3 = rsi3.Valeur.ToString("F4", inv);
        if (atr3.Pret) a3 = atr3.Valeur.ToString("F4", inv);
    }
    if (s9 != "" || r3 != "" || a5 != "" || a3 != "")
        sortie.WriteLine($"{b.FinUtc:yyyy-MM-dd HH:mm};{s9};{s21};{a5};{r3};{a3}");
}
Console.WriteLine("OK");
