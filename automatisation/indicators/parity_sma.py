#!/usr/bin/env python3
"""Test de parité SMA : valeurs de l'indicateur C# Quantower vs calcul Python.

Coté Python = référence : SMA rapide/lente = moyenne simple des N derniers closes
(fenêtre glissante incluant la barre courante), sur le MEME contrat 1H.csv que le pipeline.
Coté Quantower = CSV exporté par l'indicateur `SMA Cross NQ` (time,sma_rapide,sma_lente,signal),
calculé sur les barres du GRAPHE NQ (flux Rithmic live).

La SMA n'a aucune ambiguïté de seed/lissage → les deux DOIVENT coïncider ; tout écart révèle une
différence de DONNÉES (agrégation OHLCV / bornes de barre / fuseau), pas de formule.

  python parity_sma.py                 # référence + compare si l'export Quantower existe
  python parity_sma.py --rapide 50 --lente 200
"""
from __future__ import annotations
import argparse
import csv
import os

REF_CSV = r"F:\data\ohlcv\NQ-2026-09\1H.csv"
QT_CSV = r"F:\data\parity\NQ-sma-quantower.csv"


def sma_series(closes: list[float], n: int) -> list[float | None]:
    out: list[float | None] = []
    s = 0.0
    for i, c in enumerate(closes):
        s += c
        if i >= n:
            s -= closes[i - n]
        out.append(s / n if i >= n - 1 else None)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--rapide", type=int, default=50)
    ap.add_argument("--lente", type=int, default=200)
    ap.add_argument("--ref", default=REF_CSV)
    ap.add_argument("--qt", default=QT_CSV)
    args = ap.parse_args()

    with open(args.ref, newline="") as f:
        rows = list(csv.DictReader(f))
    times = [r["time"][:19] for r in rows]
    closes = [float(r["close"]) for r in rows]
    ref = {t: (rp, ln, c) for t, rp, ln, c in zip(
        times, sma_series(closes, args.rapide), sma_series(closes, args.lente), closes)}
    print(f"Référence Python : {len(rows)} barres, SMA {args.rapide}/{args.lente} "
          f"(prêtes dès la barre {args.lente}).")

    if not os.path.exists(args.qt):
        print(f"\nExport Quantower absent : {args.qt}")
        print("→ Lance l'indicateur 'SMA Cross NQ' sur un graphe NQ 1H (ExportParite=on),"
              " puis relance ce script.")
        return 0

    with open(args.qt, newline="") as f:
        qt = list(csv.DictReader(f))
    print(f"Export Quantower : {len(qt)} lignes ({args.qt}).")

    n, dmax_r, dmax_l, dmax_c, sig_tot = 0, 0.0, 0.0, 0.0, 0
    manquants, closes_diff = 0, 0
    has_close = qt and "close" in qt[0]
    for row in qt:
        t = row["time"][:19]
        if t not in ref or ref[t][0] is None or ref[t][1] is None:
            manquants += 1   # hors recouvrement / SMA lente pas encore prête côté Python
            continue
        n += 1
        dmax_r = max(dmax_r, abs(float(row["sma_rapide"]) - ref[t][0]))
        dmax_l = max(dmax_l, abs(float(row["sma_lente"]) - ref[t][1]))
        if has_close:
            dc = abs(float(row["close"]) - ref[t][2])
            dmax_c = max(dmax_c, dc)
            if dc > 1e-6:
                closes_diff += 1
        if row.get("signal"):
            sig_tot += 1

    print(f"\n=== PARITÉ SMA (sur {n} barres appariées par horodatage) ===")
    if has_close:
        print(f"  écart max CLOSE      : {dmax_c:.4f} pt  ({closes_diff}/{n} barres aux closes différents)")
    print(f"  écart max SMA rapide : {dmax_r:.6f} pt")
    print(f"  écart max SMA lente  : {dmax_l:.6f} pt")
    print(f"  barres Quantower sans correspondance Python : {manquants}")
    print(f"  signaux (croisements) exportés par Quantower : {sig_tot}")
    if has_close:
        if dmax_c < 1e-6 and max(dmax_r, dmax_l) > 1e-6:
            print("  → closes IDENTIQUES mais SMA différentes = décalage de FENÊTRE "
                  "(bornes de barre / bords de session), PAS la formule.")
        elif dmax_c >= 1e-6:
            print("  → closes DIFFÉRENTS = différence de DONNÉES (agrégation OHLCV Quantower vs "
                  "notre pipeline de ticks), pas la formule.")
        else:
            print("  → PARITÉ EXACTE (closes et SMA identiques).")
    else:
        print("  (relance l'indicateur avec la nouvelle version pour exporter aussi 'close')")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
