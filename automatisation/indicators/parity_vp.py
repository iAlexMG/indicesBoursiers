#!/usr/bin/env python3
"""Test de parité Volume Profile par session : indicateur C# Quantower vs pipeline Python.

Compare l'export `NQ-vp-quantower.csv` (indicateur `VP Session NQ`, footprint natif Quantower)
à `features_vp.csv` (produit par backtests/volume_profile_features.py depuis nos ticks).
Colonnes des deux : time,session,barres,delta,poc,vah,val. Appariement par horodatage UTC.

On mesure : concordance de la SESSION, écart du DELTA, écarts POC/VAH/VAL. Des écarts sont
attendus (footprint natif Quantower vs notre agrégation de ticks ; niveaux 5 pts ; bornes de
session au 1H vs sous-briques 30 min du Python) — le test les quantifie.

  python parity_vp.py
"""
from __future__ import annotations
import argparse, csv, os

REF = r"F:\data\ohlcv\NQ-2026-09\features_vp.csv"
QT = r"F:\data\parity\NQ-vp-quantower.csv"


def load(path):
    d = {}
    for r in csv.DictReader(open(path, newline="")):
        d[r["time"][:19]] = r
    return d


def fnum(x):
    try: return float(x)
    except (TypeError, ValueError): return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", default=REF)
    ap.add_argument("--qt", default=QT)
    args = ap.parse_args()

    ref = load(args.ref)
    print(f"Référence Python (features_vp) : {len(ref)} barres.")
    if not os.path.exists(args.qt):
        print(f"\nExport Quantower absent : {args.qt}")
        print("→ Ajoute l'indicateur 'VP Session NQ' sur un graphe NQ 1H, puis relance.")
        return 0
    qt = load(args.qt)
    print(f"Export Quantower (VP Session NQ) : {len(qt)} barres.")

    common = [t for t in qt if t in ref]
    n = len(common)
    sess_ok = 0
    dmax = {"delta": 0.0, "poc": 0.0, "vah": 0.0, "val": 0.0}
    dsum = {"poc": 0.0, "vah": 0.0, "val": 0.0}
    npx = 0
    for t in common:
        a, b = qt[t], ref[t]
        if a["session"] == b["session"]:
            sess_ok += 1
        # niveaux : seulement quand les deux ont un profil
        if fnum(a["poc"]) and fnum(b["poc"]):
            npx += 1
            for k in ("poc", "vah", "val"):
                d = abs(fnum(a[k]) - fnum(b[k]))
                dmax[k] = max(dmax[k], d); dsum[k] += d
        da, db = fnum(a["delta"]), fnum(b["delta"])
        if da is not None and db is not None:
            dmax["delta"] = max(dmax["delta"], abs(da - db))

    print(f"\n=== PARITÉ VP par session (sur {n} barres appariées) ===")
    print(f"  session identique       : {sess_ok}/{n}  ({100*sess_ok/n:.1f} %)" if n else "  (aucune barre commune)")
    if npx:
        print(f"  POC  : écart max {dmax['poc']:.2f} pt · moyen {dsum['poc']/npx:.2f}")
        print(f"  VAH  : écart max {dmax['vah']:.2f} pt · moyen {dsum['vah']/npx:.2f}")
        print(f"  VAL  : écart max {dmax['val']:.2f} pt · moyen {dsum['val']/npx:.2f}  (sur {npx} barres à profil)")
    print(f"  delta: écart max {dmax['delta']:.1f}")
    print("  → écarts attendus : footprint natif Quantower vs agrégation ticks ; niveaux 5 pts ;")
    print("    bornes de session au 1H (indicateur) vs sous-briques 30 min (Python).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
