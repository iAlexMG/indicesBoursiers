# Volume profile PAR SESSION sur NQ : reconstruire la dimension que l'OHLCV a écrasée,
# et l'ancrer sur les VRAIES enchères.
#
# Adapté du module frère crypto/backtesting/backtests/volume_profile_features.py
# (rapatrié le 2026-07-10 pour rendre ce pilier autonome) : mêmes algorithmes
# (footprints 30 min, value area 70 %, sessions gelées « hors »), seuls changent les
# DÉFAUTS (base de ticks NQ, niveaux de 5 pts) ; le PNG concept de la formation reste
# côté crypto.
#
# Une barre OHLCV ne connaît que le volume TOTAL de l'heure -> retour aux ticks
# (F:/data/NQ-<contrat>.db produit par ../../historique/NqExtractor, streaming). Et un
# profil « journalier » 00:00-23:59 UTC serait un ancrage ARBITRAIRE : il mélange des
# enchères distinctes. Les ancres qui comptent sont les SESSIONS (heure de New York,
# donc SENSIBLES au passage à l'heure d'été — zoneinfo s'en charge) :
#
#   Asia   18:00 -> 02:59 NY   |  London 03:00 -> 09:29 NY  |  NY 09:30 -> 16:59 NY
#   (17:00 -> 17:59 NY : hors session — niveaux GELÉS de la dernière session)
#
# Chaque session a SON profil, remis à zéro à son ouverture, puis DÉVELOPPÉ barre par
# barre : la ligne t donne l'état du profil de la session en cours, accumulé de
# l'ouverture de session à la clôture de la barre t.
#
# ⚠️ Les bornes NY tombent en MILIEU de barre 1H UTC (09:30 NY = 13:30 ou 14:30 UTC) :
# les ticks sont agrégés en sous-briques de 30 min -> frontières de session EXACTES.
#
# Produit F:/data/ohlcv/NQ-2026-09/features_vp.csv, UNE ligne par barre 1H :
#   time    = ouverture de barre (ISO UTC, même contrat que 1H.csv)
#   session = asia | london | ny | hors (session active à la CLÔTURE de la barre)
#   barres  = nombre de barres écoulées dans la session (1 = première clôture)
#   delta   = volume acheteur - vendeur de LA barre (agresseurs)
#   poc/vah/val = profil de la session EN COURS, de son ouverture à la clôture de la
#                 barre (gelés pendant « hors »)
#   ⚠️ Causalité inchangée : la ligne t n'est connaissable qu'à t+1h (end_time côté LEAN).
#
#   python backtesting/volume_profile_features.py                    # défauts NQ
#   python backtesting/volume_profile_features.py --zone rolling:24  # mode glissant
from __future__ import annotations

import argparse
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sessions import bornes_sessions   # définition UNIQUE des sessions (heure NY)

DB = "F:/data/NQ-2026-09.db"
SORTIE = "F:/data/ohlcv/NQ-2026-09/features_vp.csv"
TICK_PRIX = 5.0         # taille d'un niveau de prix : 5 pts NQ (20 ticks de 0,25)
DEMI_MS = 1_800_000     # sous-brique de 30 min : la précision des bornes de session
HEURE_MS = 3_600_000


def footprints_30min(db: str, tick: float):
    """Streaming ticks -> [(ts_ms_30min, {niveau: [vol_achat, vol_vente]}, delta)]."""
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    # trade_id croissant = ordre chronologique (contrat de la base) : pas de tri par ts.
    cur = con.execute("SELECT ts, price, size, side='buy' FROM trades ORDER BY trade_id")
    briques: list[tuple[int, dict, float]] = []
    ts_brique, prof, delta = None, None, 0.0
    for ts, prix, taille, achat in cur:
        b = ts // DEMI_MS * DEMI_MS
        if b != ts_brique:
            if ts_brique is not None:
                briques.append((ts_brique, prof, delta))
            ts_brique, prof, delta = b, {}, 0.0
        niveau = round(prix / tick) * tick
        cellule = prof.setdefault(niveau, [0.0, 0.0])
        cellule[0 if achat else 1] += taille
        delta += taille if achat else -taille
    if ts_brique is not None:
        briques.append((ts_brique, prof, delta))
    con.close()
    return briques


def value_area(profil: dict, couverture: float = 0.70):
    """POC (niveau au volume max) + Value Area : plus petite plage CONTIGUË autour du
    POC contenant `couverture` du volume total (extension gloutonne)."""
    totaux = {p: a + v for p, (a, v) in profil.items()}
    total = sum(totaux.values())
    poc = max(totaux, key=totaux.get)
    niveaux = sorted(totaux)
    lo = hi = niveaux.index(poc)
    acquis = totaux[poc]
    while acquis < couverture * total and (lo > 0 or hi < len(niveaux) - 1):
        haut = totaux[niveaux[hi + 1]] if hi < len(niveaux) - 1 else -1.0
        bas = totaux[niveaux[lo - 1]] if lo > 0 else -1.0
        if haut >= bas:
            hi += 1
            acquis += totaux[niveaux[hi]]
        else:
            lo -= 1
            acquis += totaux[niveaux[lo]]
    return poc, niveaux[hi], niveaux[lo]      # POC, VAH (haut), VAL (bas)


def _fusionner(run: dict, prof: dict) -> None:
    for niveau, (a, v) in prof.items():
        cellule = run.setdefault(niveau, [0.0, 0.0])
        cellule[0] += a
        cellule[1] += v


def ecrire_features_sessions(briques, sortie: str) -> None:
    bornes = bornes_sessions(briques[0][0], briques[-1][0])
    i = 0
    session, run = "hors", {}
    geles = ("", "", "")                      # derniers niveaux connus (gel hors session)
    barres = 0
    delta_heure = 0.0
    with open(sortie, "w", newline="") as f:
        f.write("time,session,barres,delta,poc,vah,val\n")
        for ts, prof, delta in briques:
            while i < len(bornes) and bornes[i][0] <= ts:   # frontière franchie ?
                if bornes[i][1] != session:
                    session = bornes[i][1]
                    barres = 0
                    if session != "hors":
                        run = {}              # nouvelle enchère -> profil remis à zéro
                i += 1
            if session != "hors":
                _fusionner(run, prof)         # le profil de session SE DÉVELOPPE
            delta_heure += delta
            if ts % HEURE_MS == 0:            # 1re demi-heure de la barre -> on attend
                continue
            # 2e demi-heure : la barre 1H se clôture -> émettre la ligne
            t = datetime.fromtimestamp((ts - DEMI_MS) / 1000, tz=timezone.utc)
            if session != "hors" and run:
                barres += 1
                geles = value_area(run)
            poc, vah, val = geles
            f.write(f"{t:%Y-%m-%d %H:%M:%S}+00:00,{session},{barres},{delta_heure},"
                    f"{poc},{vah},{val}\n")
            delta_heure = 0.0


def ecrire_features_rolling(briques, sortie: str, fenetre: int) -> None:
    """Ancien mode : profil glissant des `fenetre` dernières barres 1H (comparaison)."""
    from collections import deque
    fen: deque = deque(maxlen=2 * fenetre)    # en sous-briques de 30 min
    delta_heure = 0.0
    with open(sortie, "w", newline="") as f:
        f.write("time,session,barres,delta,poc,vah,val\n")
        for ts, prof, delta in briques:
            fen.append(prof)
            delta_heure += delta
            if ts % HEURE_MS == 0:
                continue
            t = datetime.fromtimestamp((ts - DEMI_MS) / 1000, tz=timezone.utc)
            if len(fen) == 2 * fenetre:
                run: dict = {}
                for p in fen:
                    _fusionner(run, p)
                poc, vah, val = value_area(run)
                f.write(f"{t:%Y-%m-%d %H:%M:%S}+00:00,rolling,{fenetre},{delta_heure},"
                        f"{poc},{vah},{val}\n")
            else:
                f.write(f"{t:%Y-%m-%d %H:%M:%S}+00:00,rolling,0,{delta_heure},,,\n")
            delta_heure = 0.0


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--tick", type=float, default=TICK_PRIX,
                    help="taille d'un niveau de prix (défaut : 5 pts NQ)")
    ap.add_argument("--db", default=DB, help="base de ticks (défaut : NQ-2026-09)")
    ap.add_argument("--out", default=SORTIE, help="CSV de sortie (défaut : NQ-2026-09)")
    ap.add_argument("--zone", default="sessions",
                    help="sessions (défaut : Asia/London/NY, heure de New York) | rolling:N")
    args = ap.parse_args()

    print("streaming des ticks (une passe, sous-briques de 30 min)…")
    briques = footprints_30min(args.db, args.tick)
    print(f"  {len(briques)} sous-briques de 30 min, "
          f"{sum(len(p) for _, p, _ in briques)} cellules (niveau, brique)")

    if args.zone == "sessions":
        ecrire_features_sessions(briques, args.out)
    else:
        ecrire_features_rolling(briques, args.out, int(args.zone.partition(":")[2] or 24))
    print(f"features -> {args.out} (zone {args.zone}, tick {args.tick:.0f})")


if __name__ == "__main__":
    main()
