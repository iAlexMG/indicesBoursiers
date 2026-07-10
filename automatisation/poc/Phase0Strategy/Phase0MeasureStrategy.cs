using System.Globalization;
using System.Text;
using TradingPlatform.BusinessLayer;

namespace Phase0Measure;

/// <summary>
/// Phase 0 — outil de mesure tournant DANS Quantower (connexion Rithmic déjà authentifiée).
/// Répond aux questions Q2–Q4 du POC + spécifications du contrat, et écrit un rapport disque.
/// À déployer dans C:\Quantower\Settings\Scripts\Strategies\ puis lancer depuis le panneau
/// Strategies en ayant sélectionné le symbole NQ (contrat front).
/// </summary>
public sealed class Phase0MeasureStrategy : Strategy
{
    [InputParameter("Symbole (NQ front)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Jours à sonder (recul max)", 1, 1, 400, 1, 0)]
    public int ProbeDays = 45;

    [InputParameter("Arrêt après N jours vides consécutifs", 2, 1, 30, 1, 0)]
    public int EmptyStop = 3;

    [InputParameter("Fichier rapport", 3)]
    public string ReportPath =
        @"C:\Users\Moi\Desktop\Claude_Code\Portfolio\Quantower\docs\phase0-measures.txt";

    public Phase0MeasureStrategy() => Name = "Phase0 Measure (NQ)";

    protected override void OnRun()
    {
        try { Measure(); }
        catch (Exception ex) { this.LogError($"Phase0 EXCEPTION : {ex}"); }
    }

    private void Measure()
    {
        var sb = new StringBuilder();
        void W(string line) { sb.AppendLine(line); this.LogInfo(line); }

        W($"# Phase 0 — mesures NQ via Rithmic (in-Quantower)  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

        var s = Instrument;
        if (s is null) { W("ERREUR : aucun symbole sélectionné (choisir NQ dans les paramètres)."); Flush(sb); return; }

        // --- Spécifications du contrat (mesurées, pas supposées) ---
        W("");
        W("## Contrat");
        W($"Name={s.Name} | Id={s.Id} | Exchange={s.Exchange?.ExchangeName} | Type={s.SymbolType}");
        W($"TickSize={Fmt(s.TickSize)} | GetTickCost(1)={Fmt(s.GetTickCost(1))} | NotionalValueStep={Fmt(s.NotionalValueStep)}");
        W($"LotSize={Fmt(s.LotSize)} | MinLot={Fmt(s.MinLot)} | MaxLot={Fmt(s.MaxLot)} | LotStep={Fmt(s.LotStep)}");
        W($"ExpirationDate={s.ExpirationDate:o} | LastTradingDate={s.LastTradingDate:o} | Root={s.Root}");

        // --- Métadonnées d'historique (Q2/Q3 : ce que le vendor annonce) ---
        var md = s.HistoryMetadata;
        W("");
        W("## HistoryMetadata (annoncé par Rithmic)");
        if (md is not null)
        {
            W($"tickHistoryTypes=[{Join(md.AllowedHistoryTypesHistoryAggregationTick)}]");
            W($"timeHistoryTypes=[{Join(md.AllowedHistoryTypesHistoryAggregationTime)}]");
            W($"basePeriods=[{Join(md.AllowedBasePeriodsHistoryAggregationTime)}]");
            W($"stepTick={md.DownloadingStep_Tick} | stepMin={md.DownloadingStep_Minute} | stepDay={md.DownloadingStep_Day}");
            W($"ServerSideTickDirectionAvailable={md.ServerSideTickDirectionAvailable}");
        }
        else W("HistoryMetadata = null");

        // --- Q2 profondeur + Q4 volumétrie : sonde jour par jour en reculant ---
        W("");
        W("## Q2 profondeur tick (Last) + Q4 volume/jour — sonde jour par jour");
        var nowUtc = DateTime.UtcNow;
        DateTime? earliest = null;
        long grandTotal = 0, grandBuy = 0, grandSell = 0, grandNone = 0;
        int emptyStreak = 0, daysWithData = 0;

        for (int d = 0; d < ProbeDays; d++)
        {
            var to = nowUtc.Date.AddDays(-d);
            var from = to.AddDays(-1);
            long total = 0, buy = 0, sell = 0, none = 0;
            DateTime? dayEarliest = null;

            HistoricalData? hd = null;
            try
            {
                hd = s.GetTickHistory(HistoryType.Last, from, to);
                foreach (var raw in hd)
                {
                    if (raw is not HistoryItemLast t) continue;
                    total++;
                    switch (t.AggressorFlag)
                    {
                        case AggressorFlag.Buy: buy++; break;
                        case AggressorFlag.Sell: sell++; break;
                        default: none++; break;
                    }
                    var tt = t.TimeLeft;
                    if (dayEarliest is null || tt < dayEarliest) dayEarliest = tt;
                }
            }
            finally { hd?.Dispose(); }

            if (total == 0)
            {
                emptyStreak++;
                W($"J-{d,2} [{from:yyyy-MM-dd}] : 0 tick");
                if (emptyStreak >= EmptyStop) { W($"→ {EmptyStop} jours vides consécutifs : mur d'historique atteint, arrêt."); break; }
                continue;
            }
            emptyStreak = 0;
            daysWithData++;
            grandTotal += total; grandBuy += buy; grandSell += sell; grandNone += none;
            if (dayEarliest is not null && (earliest is null || dayEarliest < earliest)) earliest = dayEarliest;
            double pctSigned = total > 0 ? 100.0 * (buy + sell) / total : 0;
            W($"J-{d,2} [{from:yyyy-MM-dd}] : {total,8} ticks | buy={buy} sell={sell} none={none} | aggressor renseigné={pctSigned:F1}%");
        }

        // --- Synthèse Q2/Q3/Q4 ---
        W("");
        W("## Synthèse");
        W($"Q2 profondeur : plus ancien tick vu = {(earliest?.ToString("o") ?? "aucun")} ({daysWithData} jours avec données)");
        double pctAll = grandTotal > 0 ? 100.0 * (grandBuy + grandSell) / grandTotal : 0;
        W($"Q3 aggressor : total={grandTotal} | buy={grandBuy} sell={grandSell} none={grandNone} → renseigné {pctAll:F1}%");
        long perDay = daysWithData > 0 ? grandTotal / daysWithData : 0;
        W($"Q4 volumétrie : ~{perDay} ticks/jour (moyenne sur {daysWithData} jours) → SQLite ~{perDay * 40 / 1_000_000.0:F1} Mo/jour (40 o/tick estimé)");

        Flush(sb);
        W($"Rapport écrit : {ReportPath}");
    }

    private void Flush(StringBuilder sb)
    {
        try
        {
            var dir = Path.GetDirectoryName(ReportPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ReportPath, sb.ToString());
        }
        catch (Exception ex) { this.LogError($"Écriture rapport échouée ({ReportPath}) : {ex.Message}"); }
    }

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);
    private static string Join<T>(IEnumerable<T>? xs) => xs is null ? "" : string.Join(",", xs);
}
