# Volume profile PAR SESSION sur NQ : reconstruire la dimension que l'OHLCV a écrasée,
# et l'ancrer sur les VRAIES enchères.
#
# Adapté du module frère crypto/backtesting/backtests/volume_profile_features.py
# (rapatrié le 2026-07-10, refonte cadence 1 min reportée le 2026-07-19) : mêmes
# algorithmes (footprints, value area 70 %, sessions gelées « hors »), seuls changent
# les DÉFAUTS (base de ticks NQ, niveaux de 5 pts) ; le PNG concept de la formation
# reste côté crypto.
#
# Une barre OHLCV ne connaît que le volume TOTAL de sa période -> retour aux ticks
# (H:/IndicesBoursiers/historique/NQ-<contrat>.db produit par ../../historique/NqExtractor,
# streaming, jamais tout en RAM). Et un profil « journalier » 00:00-23:59 UTC serait un
# ancrage ARBITRAIRE : il mélange des enchères distinctes. Les ancres qui comptent sont
# les SESSIONS (heure de New York, donc SENSIBLES au passage à l'heure d'été —
# zoneinfo s'en charge) :
#
#   Asia   18:00 -> 02:59 NY   |  London 03:00 -> 09:29 NY  |  NY 09:30 -> 16:59 NY
#   (17:00 -> 17:59 NY : hors session — niveaux GELÉS de la dernière session)
#
# Chaque session a SON profil, remis à zéro à son ouverture, puis DÉVELOPPÉ barre par
# barre : la ligne t donne l'état du profil de la session en cours, accumulé de
# l'ouverture de session à la clôture de la barre t (les « sous-VP » intra-session :
# on voit le POC/VAH/VAL se déplacer à mesure que l'enchère se construit).
#
# Produit H:/IndicesBoursiers/historique/ohlcv/NQ-2026-09/features_vp.csv, UNE ligne par
# barre 1 MIN (cadence scalping 1 m : sous-briques 1 min -> 1 ligne/minute, alignée sur
# 1m.csv). Le profil de session se développe minute par minute (value_area recalculée à
# chaque minute). L'ancien mode `rolling` reste en sous-briques 30 min (legacy).
#   time    = ouverture de barre 1 min (ISO UTC, même contrat que 1m.csv)
#   session = asia | london | ny | hors (session active à la CLÔTURE de la minute)
#   barres  = nombre de MINUTES écoulées dans la session (1 = première clôture)
#   delta   = volume acheteur - vendeur de LA minute (agresseurs, en contrats)
#   poc/vah/val = profil de la session EN COURS, de son ouverture à la clôture de la
#                 minute (gelés pendant « hors »)
#   ⚠️ Causalité : la ligne t n'est connaissable qu'à t+1min (end_time côté LEAN).
#
#   python backtesting/backtests/volume_profile_features.py                  # défauts NQ
#   python backtesting/backtests/volume_profile_features.py --zone rolling:24
from __future__ import annotations

import argparse
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sessions import bornes_sessions   # définition UNIQUE des sessions (heure NY)

DB = "H:/IndicesBoursiers/historique/NQ-2026-09.db"
SORTIE = "H:/IndicesBoursiers/historique/ohlcv/NQ-2026-09/features_vp.csv"
TICK_PRIX = 5.0         # taille d'un niveau de prix : 5 pts NQ (20 ticks de 0,25)
MINUTE_MS = 60_000      # sous-brique de 1 min : la cadence scalping (bornes exactes)
DEMI_MS = 1_800_000     # sous-brique de 30 min : cadence de l'ancien mode rolling
HEURE_MS = 3_600_000


def footprints(db: str, tick: float, pas_ms: int):
    """Streaming ticks -> [(ts_ms_pas, {niveau: [vol_achat, vol_vente]}, delta)] agrégés
    par pas_ms (1 min pour les sessions, 30 min pour le mode rolling)."""
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    # trade_id croissant = ordre chronologique (contrat de la base) : pas de tri par ts.
    cur = con.execute("SELECT ts, price, size, side='buy' FROM trades ORDER BY trade_id")
    briques: list[tuple[int, dict, float]] = []
    ts_brique, prof, delta = None, None, 0.0
    for ts, prix, taille, achat in cur:
        b = ts // pas_ms * pas_ms
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
    """Cadence 1 MINUTE : une ligne par sous-brique (= par minute). Le profil de la
    session en cours se développe minute par minute et value_area est recalculée à
    chaque minute (les « sous-VP » intra-session bougent à la minute). Delta par minute."""
    bornes = bornes_sessions(briques[0][0], briques[-1][0])
    i = 0
    session, run = "hors", {}
    geles = ("", "", "")                      # derniers niveaux connus (gel hors session)
    barres = 0
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
                if run:
                    barres += 1
                    geles = value_area(run)   # POC/VAH/VAL recalculés à la minute
            poc, vah, val = geles
            t = datetime.fromtimestamp(ts / 1000, tz=timezone.utc)
            f.write(f"{t:%Y-%m-%d %H:%M:%S}+00:00,{session},{barres},{delta},"
                    f"{poc},{vah},{val}\n")


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

    # Sessions = cadence 1 min ; rolling (legacy) = sous-briques 30 min.
    pas_ms = MINUTE_MS if args.zone == "sessions" else DEMI_MS
    print(f"streaming des ticks (une passe, sous-briques de {pas_ms // 60000} min)…")
    briques = footprints(args.db, args.tick, pas_ms)
    print(f"  {len(briques)} sous-briques, "
          f"{sum(len(p) for _, p, _ in briques)} cellules (niveau, brique)")

    if args.zone == "sessions":
        ecrire_features_sessions(briques, args.out)
    else:
        ecrire_features_rolling(briques, args.out, int(args.zone.partition(":")[2] or 24))
    print(f"features -> {args.out} (zone {args.zone}, tick {args.tick:.0f})")


if __name__ == "__main__":
    main()
