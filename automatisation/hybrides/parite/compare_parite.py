# Parité indicateurs : C# (Hybrides/Indicateurs.cs) vs LEAN (journaux des jumeaux volet C).
# Pour chaque événement de journal portant des indicateurs, on compare à la valeur C#
# au même horodatage (H1 : dernière borne 5 m <= ts, son signal tombe sur des minutes 1 m).
import csv
import json
from datetime import datetime
from pathlib import Path

CSHARP = Path(__file__).parent / "parite_csharp.csv"
JOURNAUX = Path(__file__).resolve().parents[3] / "backtesting" / "backtests" / "journaux"

# ts -> (sma9, sma21, atr5, rsi3, atr3), et liste triée des bornes 5 m pour le "dernier <=".
valeurs: dict[str, tuple] = {}
bornes5: list[str] = []
with open(CSHARP) as f:
    for row in csv.DictReader(f, delimiter=";"):
        valeurs[row["ts"]] = row
        if row["atr5"]:
            bornes5.append(row["ts"])

def derniere_borne5(ts: str) -> str | None:
    import bisect
    i = bisect.bisect_right(bornes5, ts)
    return bornes5[i - 1] if i else None

def flt(s):
    return float(s) if s else None

TOL = 0.011  # les journaux sont arrondis à 2 décimales

ecarts, comparaisons = [], 0
for strat, champs in [
    ("sma_suiveur_nq", [("sma_rapide", "sma9", "exact"), ("sma_lente", "sma21", "exact"), ("atr", "atr5", "exact")]),
    ("rsi_bracket_nq", [("rsi", "rsi3", "exact"), ("atr", "atr3", "exact")]),
    ("orb_nq", [("atr", "atr5", "borne5")]),
]:
    for fichier in sorted((JOURNAUX / strat).glob("*.ndjson")):
        for ligne in open(fichier, encoding="utf-8"):
            ev = json.loads(ligne)
            indic = ev.get("indicateurs") or {}
            ts = datetime.fromisoformat(ev["ts"]).strftime("%Y-%m-%d %H:%M")
            for cle_lean, col_cs, mode in champs:
                if cle_lean not in indic:
                    continue
                ts_cs = ts if mode == "exact" else derniere_borne5(ts)
                v_cs = flt(valeurs.get(ts_cs or "", {}).get(col_cs)) if ts_cs else None
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
    # au-delà : distribution
    maxi = max(e[5] for e in ecarts)
    print(f"écart max : {maxi}")
else:
    print("PARITÉ OK — aucun écart au-delà de l'arrondi des journaux")
