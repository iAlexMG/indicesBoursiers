#!/usr/bin/env python3
"""Produit les CSV OHLCV canoniques LEAN, en fusionnant deux sources NQ :

1. (prioritaire) les CSV de candles.py — reconstruits depuis les TICKS, donc avec
   buy_volume — au format : ts,date_utc,open,high,low,close,volume,buy_volume,sell_volume,trades
2. (complément)  la base de BARRES MINUTE de « NQ Bars Extractor » (--bars-db) —
   bien plus profonde que la fenêtre de ticks Rithmic (~2-3 semaines), mais sans
   côté agresseur : buy_volume reste vide sur ces lignes.

LEAN (lecteur PythonData) attend : time,open,high,low,close,volume,buy_volume,trades
  avec time = 'YYYY-MM-DD HH:MM:SS+00:00' (ISO UTC = ouverture de barre).
Buckets : planchers UTC (1H ; 4H alignées 00/04/…, D = minuit UTC) — mêmes bornes
que candles.py (chaque borne 4H/D coïncide avec une borne 1H).

Règle de fusion : à horodatage égal, la ligne issue des TICKS gagne (plus riche).
⚠️ À la frontière de couverture, le premier bucket 4H/D tick peut être partiel
(ticks démarrant en cours de bucket) : erreur bornée à 1 bucket par pas de temps.

Produit H:\\IndicesBoursiers\\historique\\ohlcv\\<dir>\\{1H,4H,D}.csv.

Exemples :
  python normalize_ohlcv.py --dir H:\\IndicesBoursiers\\historique\\ohlcv\\NQ-2026-09 --prefix NQ-CME
  python normalize_ohlcv.py --dir H:\\IndicesBoursiers\\historique\\ohlcv\\NQ-2026-09 --prefix NQ-CME \\
                            --bars-db H:\\IndicesBoursiers\\historique\\NQ-2026-09-1m.db
"""
from __future__ import annotations
import argparse
import csv
import datetime as dt
import os
import sqlite3

TFS = ["1H", "4H", "D"]
HOUR_MS = 3_600_000
TF_MS = {"1H": HOUR_MS, "4H": 4 * HOUR_MS, "D": 24 * HOUR_MS}


def iso_utc(ts_ms: int) -> str:
    t = dt.datetime.fromtimestamp(ts_ms / 1000, dt.timezone.utc)
    return t.strftime("%Y-%m-%d %H:%M:%S") + "+00:00"


def lire_candles(src: str) -> dict[str, list]:
    """CSV candles.py (ticks) -> {time_iso: ligne canonique}."""
    lignes: dict[str, list] = {}
    with open(src, newline="", encoding="utf-8") as fi:
        r = csv.reader(fi)
        next(r, None)  # ts,date_utc,open,high,low,close,volume,buy_volume,sell_volume,trades
        for row in r:
            if not row:
                continue
            time_iso = iso_utc(int(row[0]))
            # open=2 high=3 low=4 close=5 volume=6 buy_volume=7 trades=9
            lignes[time_iso] = [time_iso, row[2], row[3], row[4], row[5], row[6], row[7], row[9]]
    return lignes


def agreger_bars(bars_db: str, tf: str) -> dict[str, list]:
    """Barres minute (NQ Bars Extractor) -> {time_iso: ligne canonique} au pas `tf`.
    buy_volume inconnu (pas d'agresseur dans une barre) -> champ vide."""
    largeur = TF_MS[tf]
    con = sqlite3.connect(f"file:{bars_db}?mode=ro", uri=True)
    cur = con.execute("SELECT ts, open, high, low, close, volume, ticks FROM bars ORDER BY ts")
    lignes: dict[str, list] = {}
    bucket = None
    o = h = l = c = v = n = 0
    for ts, bo, bh, bl, bc, bv, bn in cur:
        b = ts // largeur * largeur
        if b != bucket:
            if bucket is not None:
                lignes[iso_utc(bucket)] = [iso_utc(bucket), o, h, l, c, v, "", n]
            bucket, o, h, l, c, v, n = b, bo, bh, bl, bc, 0.0, 0
        h = max(h, bh)
        l = min(l, bl)
        c = bc
        v += bv
        n += bn
    if bucket is not None:
        lignes[iso_utc(bucket)] = [iso_utc(bucket), o, h, l, c, v, "", n]
    con.close()
    return lignes


def ecrire(dst: str, lignes: dict[str, list]) -> int:
    with open(dst, "w", newline="", encoding="utf-8") as fo:
        w = csv.writer(fo)
        w.writerow(["time", "open", "high", "low", "close", "volume", "buy_volume", "trades"])
        for time_iso in sorted(lignes):
            w.writerow(lignes[time_iso])
    return len(lignes)


def main() -> int:
    p = argparse.ArgumentParser(description="Fusionne ticks + barres minute -> CSV canoniques LEAN.")
    p.add_argument("--dir", required=True, help="dossier des CSV (in & out)")
    p.add_argument("--prefix", required=True, help="préfixe des CSV candles.py (ex. NQ-CME)")
    p.add_argument("--bars-db", help="base de barres minute (NQ Bars Extractor) — optionnelle")
    args = p.parse_args()

    for tf in TFS:
        # 1) le complément profond d'abord (barres minute)…
        lignes: dict[str, list] = {}
        if args.bars_db:
            if not os.path.exists(args.bars_db):
                raise SystemExit(f"--bars-db introuvable : {args.bars_db}")
            lignes = agreger_bars(args.bars_db, tf)
            n_bars = len(lignes)
        else:
            n_bars = 0

        # 2) …recouvert par les lignes issues des ticks (prioritaires).
        src = os.path.join(args.dir, f"{args.prefix}-{tf}.csv")
        n_ticks = 0
        if os.path.exists(src):
            ticks = lire_candles(src)
            n_ticks = len(ticks)
            lignes.update(ticks)
        elif not args.bars_db:
            print(f"  (absent, ignoré) {src}")
            continue

        dst = os.path.join(args.dir, f"{tf}.csv")
        total = ecrire(dst, lignes)
        detail = f"{n_ticks} ticks-barres" + (f" + {total - n_ticks} barres-minute" if n_bars else "")
        print(f"  {tf}: {total} barres ({detail}) -> {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
