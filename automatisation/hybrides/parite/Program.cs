// Parité indicateurs C# (Hybrides) vs LEAN (jumeaux, refonte 1 m du 2026-07-20) : rejoue le
// CSV 1 m canonique dans les classes de Indicateurs.cs et imprime, à chaque barre 1 m, les
// valeurs du déclencheur commun (SMA 9/21) et de l'ATR14 — tout sur 1 m.
// Sortie : ts_fin_utc;sma9;sma21;atr  (InvariantCulture)
using System.Globalization;
using Hybrides;

var inv = CultureInfo.InvariantCulture;
string csv = args.Length > 0 ? args[0] : @"H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09\1m.csv";
var de = DateTime.Parse(args.Length > 1 ? args[1] : "2026-06-01", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
var a = DateTime.Parse(args.Length > 2 ? args[2] : "2026-07-11", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

var cross = new DeclencheurSmaCross(9, 21);
var atr = new AtrWilder(14);

using var sortie = new StreamWriter(args.Length > 3 ? args[3] : "parite_csharp.csv");
sortie.WriteLine("ts;sma9;sma21;atr");
foreach (var ligne in File.ReadLines(csv))
{
    if (ligne.Length == 0 || !char.IsDigit(ligne[0])) continue;
    var c = ligne.Split(',');
    var ouverture = DateTime.ParseExact(c[0][..19], "yyyy-MM-dd HH:mm:ss", inv,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    if (ouverture < de) continue;
    if (ouverture >= a) break;
    double high = double.Parse(c[2], inv), low = double.Parse(c[3], inv), close = double.Parse(c[4], inv);
    var finUtc = ouverture.AddMinutes(1);
    atr.Ajouter(high, low, close);
    cross.Ajouter(close);
    string s9 = cross.Pret ? cross.Rapide.ToString("F4", inv) : "";
    string s21 = cross.Pret ? cross.Lente.ToString("F4", inv) : "";
    string a14 = atr.Pret ? atr.Valeur.ToString("F4", inv) : "";
    if (s9 != "" || a14 != "")
        sortie.WriteLine($"{finUtc:yyyy-MM-dd HH:mm};{s9};{s21};{a14}");
}
Console.WriteLine("OK");
