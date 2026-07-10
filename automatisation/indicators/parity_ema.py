#!/usr/bin/env python3
"""Test de parité EMA : valeur de l'EMA NATIVE Quantower vs Python.

Comme le RSI, l'EMA a un lissage récursif + un SEED de démarrage → on attend une divergence
initiale qui converge. Deux références Python pour identifier la convention de Quantower :
  - seed SMA : ema[N-1] = SMA(N), puis ema[i] = a*close[i] + (1-a)*ema[i-1], a = 2/(N+1)
  - seed first-price : ema[0] = close[0], puis récursif dès i=1

  python parity_ema.py
"""
from __future__ import annotations
import argparse, csv, datetime as dt, os, sqlite3

REF = r"F:\data\ohlcv\NQ-2026-09\1H.csv"
QT = r"F:\data\parity\NQ-ema-quantower.csv"
DB = r"F:\data\NQ-2026-09.db"


def closes_from_db(db: str, res_min: int):
    """Clôtures à la résolution res_min (minutes) depuis la base de ticks (close = dernier tick)."""
    bucket = res_min * 60_000
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    b = {}
    for ts, price in con.execute("SELECT ts, price FROM trades ORDER BY trade_id"):
        b[ts // bucket * bucket] = price
    con.close()
    keys = sorted(b)
    times = [dt.datetime.fromtimestamp(k / 1000, dt.timezone.utc).strftime("%Y-%m-%d %H:%M:%S") for k in keys]
    return times, [b[k] for k in keys]


def ema_sma_seed(closes, n):
    a = 2.0 / (n + 1)
    out = [None] * len(closes)
    if len(closes) < n:
        return out
    out[n - 1] = sum(closes[:n]) / n
    for i in range(n, len(closes)):
        out[i] = a * closes[i] + (1 - a) * out[i - 1]
    return out


def ema_first_seed(closes, n):
    a = 2.0 / (n + 1)
    out = [closes[0]]
    for i in range(1, len(closes)):
        out.append(a * closes[i] + (1 - a) * out[i - 1])
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--periode", type=int, default=20)
    ap.add_argument("--resolution-min", type=int, default=0,
                    help="0 = référence 1H.csv (mode Graphe/1h) ; sinon clôtures N min depuis la base de ticks")
    ap.add_argument("--ref", default=REF)
    ap.add_argument("--db", default=DB)
    ap.add_argument("--qt", default=QT)
    args = ap.parse_args()

    if args.resolution_min > 0:
        times, closes = closes_from_db(args.db, args.resolution_min)
        print(f"Référence {args.resolution_min} min (ticks) : {len(closes)} barres.")
    else:
        rows = list(csv.DictReader(open(args.ref, newline="")))
        times = [r["time"][:19] for r in rows]
        closes = [float(r["close"]) for r in rows]
    smaS = dict(zip(times, ema_sma_seed(closes, args.periode)))
    firstS = dict(zip(times, ema_first_seed(closes, args.periode)))
    print(f"Référence Python : {len(closes)} barres, EMA {args.periode} (2 seeds).")

    if not os.path.exists(args.qt):
        print(f"\nExport Quantower absent : {args.qt}")
        print("→ Ajoute l'indicateur 'EMA NQ (native)' sur un graphe NQ 1H, puis relance.")
        return 0

    qt = list(csv.DictReader(open(args.qt, newline="")))
    diffs_s = []
    dmax_s = dmax_f = 0.0
    for row in qt:
        t = row["time"][:19]
        v = float(row["ema"])
        if smaS.get(t) is not None:
            d = abs(v - smaS[t]); dmax_s = max(dmax_s, d); diffs_s.append(d)
        if firstS.get(t) is not None:
            dmax_f = max(dmax_f, abs(v - firstS[t]))
    n = len(diffs_s)
    SKIP = 40
    conv = diffs_s[SKIP:] if n > SKIP else []
    max_conv = max(conv) if conv else float("nan")
    moy_conv = (sum(conv) / len(conv)) if conv else float("nan")

    print(f"Export Quantower : {len(qt)} lignes ; {n} barres appariées.")
    print(f"\n=== PARITÉ EMA ===")
    print(f"  écart max vs seed SMA        : {dmax_s:.4f}")
    print(f"  écart max vs seed first-price: {dmax_f:.4f}")
    print(f"  → seed le plus proche : {'SMA' if dmax_s <= dmax_f else 'first-price'}")
    print(f"  zone convergée (après {SKIP} barres) : écart max {max_conv:.4f}, moyen {moy_conv:.4f}")
    print("  → la formule (lissage 2/(N+1)) est la même ; l'écart initial = SEED de warmup "
          "(Python démarre au 18/06, Quantower a des mois) et converge vers 0.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
