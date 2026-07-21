# Parité indicateurs : C# (Hybrides/Indicateurs.cs) vs LEAN (journaux des jumeaux).
# Refonte 2026-07-20 : tout est sur 1 m (SMA 9/21 + ATR14), donc chaque événement de journal
# portant des indicateurs se compare DIRECTEMENT à la valeur C# du même horodatage.
import csv
import json
from datetime import datetime
from pathlib import Path

CSHARP = Path(__file__).parent / "parite_csharp.csv"
JOURNAUX = Path(__file__).resolve().parents[3] / "backtesting" / "backtests" / "journaux"
CHAMPS = [("sma_rapide", "sma9"), ("sma_lente", "sma21"), ("atr", "atr")]
STRATS = ["sma_bracket_nq", "sma_suiveur_nq", "sma_annule_nq"]
TOL = 0.011  # les journaux sont arrondis à 2 décimales

valeurs = {}
with open(CSHARP) as f:
    for row in csv.DictReader(f, delimiter=";"):
        valeurs[row["ts"]] = row

def flt(s):
    return float(s) if s else None

ecarts, comparaisons = [], 0
for strat in STRATS:
    dossier = JOURNAUX / strat
    if not dossier.exists():
        print(f"{strat}: (aucun journal)")
        continue
    for fichier in sorted(dossier.glob("*.ndjson")):
        for ligne in open(fichier, encoding="utf-8"):
            ev = json.loads(ligne)
            indic = ev.get("indicateurs") or {}
            ts = datetime.fromisoformat(ev["ts"]).strftime("%Y-%m-%d %H:%M")
            for cle_lean, col_cs in CHAMPS:
                if cle_lean not in indic:
                    continue
                v_cs = flt(valeurs.get(ts, {}).get(col_cs))
                if v_cs is None:
                    continue
                comparaisons += 1
                d = abs(indic[cle_lean] - v_cs)
                if d > TOL:
                    ecarts.append((strat, ts, cle_lean, indic[cle_lean], round(v_cs, 4), round(d, 4)))

print(f"{comparaisons} comparaisons LEAN vs C#, tolérance {TOL}")
if ecarts:
    print(f"ÉCARTS : {len(ecarts)}")
    for e in ecarts[:15]:
        print("  ", e)
    print(f"écart max : {max(e[5] for e in ecarts)}")
else:
    print("PARITÉ OK — aucun écart au-delà de l'arrondi des journaux")
