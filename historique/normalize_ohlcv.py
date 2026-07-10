#!/usr/bin/env python3
"""Normalise les CSV OHLCV de candles.py au format canonique LEAN du projet frère.

candles.py écrit : ts,date_utc,open,high,low,close,volume,buy_volume,sell_volume,trades
LEAN (lecteur PythonData) attend : time,open,high,low,close,volume,buy_volume,trades
  avec time = 'YYYY-MM-DD HH:MM:SS+00:00' (ISO UTC = ouverture de barre).

Produit F:\\data\\ohlcv\\<dir>\\{1H,4H,D}.csv à partir de <dir>\\<PREFIX>-{1H,4H,D}.csv.

Exemple :
  python normalize_ohlcv.py --dir F:\\data\\ohlcv\\NQ-2026-09 --prefix NQ-CME
"""
from __future__ import annotations
import argparse
import csv
import datetime as dt
import os

TFS = ["1H", "4H", "D"]


def normalize(src: str, dst: str) -> int:
    n = 0
    with open(src, newline="", encoding="utf-8") as fi, \
         open(dst, "w", newline="", encoding="utf-8") as fo:
        r = csv.reader(fi)
        w = csv.writer(fo)
        w.writerow(["time", "open", "high", "low", "close", "volume", "buy_volume", "trades"])
        header = next(r, None)  # ts,date_utc,open,high,low,close,volume,buy_volume,sell_volume,trades
        for row in r:
            if not row:
                continue
            ts = int(row[0])
            t = dt.datetime.fromtimestamp(ts / 1000, dt.timezone.utc)
            time_iso = t.strftime("%Y-%m-%d %H:%M:%S") + "+00:00"
            # open=2 high=3 low=4 close=5 volume=6 buy_volume=7 trades=9
            w.writerow([time_iso, row[2], row[3], row[4], row[5], row[6], row[7], row[9]])
            n += 1
    return n


def main() -> int:
    p = argparse.ArgumentParser(description="Normalise candles.py -> format canonique LEAN.")
    p.add_argument("--dir", required=True, help="dossier des CSV (in & out)")
    p.add_argument("--prefix", required=True, help="préfixe des CSV candles.py (ex. NQ-CME)")
    args = p.parse_args()
    for tf in TFS:
        src = os.path.join(args.dir, f"{args.prefix}-{tf}.csv")
        if not os.path.exists(src):
            print(f"  (absent, ignoré) {src}")
            continue
        dst = os.path.join(args.dir, f"{tf}.csv")
        n = normalize(src, dst)
        print(f"  {tf}: {n} barres -> {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
