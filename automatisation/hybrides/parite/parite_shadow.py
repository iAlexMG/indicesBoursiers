# Parité PHASE 4 : décisions LIVE (journal shadow) vs jumeau BACKTEST du même jour.
#
# But : prouver qu'en réel la stratégie DÉCIDE comme au backtest. On compare les événements
# `signal` (le croisement + son sens) minute par minute, dans la ou les fenêtres où le shadow
# a tourné (demarrage -> arret). Deux sources d'écart possibles, que ça mesure :
#   - la logique (C# live vs LEAN jumeau) — devrait être identique (mêmes règles) ;
#   - la donnée (barres 1 m reconstruites du flux vs barres du CSV extracteur).
#
# Usage :
#   python parite_shadow.py --lister <journal.ndjson>              # dump des signaux d'un journal
#   python parite_shadow.py --slug sma_bracket_nq --date 2026-07-22
#       [--shadow-dir H:\IndicesBoursiers\automatisation\journaux]
#       [--jumeau-dir <repo>\backtesting\backtests\journaux]
#
# ⚠ Le jumeau du jour n'existe QUE si on a rejoué le jumeau LEAN sur ce jour — ce qui exige
# que le CSV 1 m couvre ce jour (donc que l'extracteur de barres ait capté ces jours, puis
# normalize_ohlcv). Tant que le CSV s'arrête avant, ce script sert surtout au --lister.
import argparse
import json
from datetime import datetime
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SHADOW_DEFAUT = Path(r"H:\IndicesBoursiers\automatisation\journaux")
JUMEAU_DEFAUT = REPO / "backtesting" / "backtests" / "journaux"


def charger(fichier):
    """-> liste d'événements (dict), triés par ts."""
    evs = []
    for ligne in open(fichier, encoding="utf-8"):
        ligne = ligne.strip()
        if ligne:
            evs.append(json.loads(ligne))
    evs.sort(key=lambda e: e["ts"])
    return evs


def sens(raison):
    if "-> long" in raison:
        return "long"
    if "-> short" in raison:
        return "short"
    return "?"


def minute(ts):
    return datetime.fromisoformat(ts).strftime("%Y-%m-%d %H:%M")


def signaux(evs, refuses=False):
    """{(minute, sens): raison} des signaux d'entrée (pas les 'REFUSÉ' sauf si demandé)."""
    out = {}
    for e in evs:
        if e["evenement"] != "signal":
            continue
        r = e["raison"]
        if "REFUSÉ" in r and not refuses:
            continue
        s = sens(r)
        if s == "?":       # sorties sur signal (croisement inverse) : pas une entrée
            continue
        out[(minute(e["ts"]), s)] = r
    return out


def fenetres(evs):
    """Fenêtres [demarrage, arret] où le shadow tournait (pour ne comparer que là)."""
    f, deb = [], None
    for e in evs:
        if e["evenement"] == "demarrage":
            deb = e["ts"]
        elif e["evenement"] == "arret" and deb:
            f.append((deb, e["ts"]))
            deb = None
    if deb:
        f.append((deb, "9999"))
    return f


def dans_fenetres(ts, f):
    return any(a <= ts <= b for a, b in f)


def lister(fichier):
    evs = charger(fichier)
    sig = signaux(evs)
    print(f"{fichier} — {len(evs)} événements, {len(sig)} signaux d'entrée :")
    for (m, s), r in sorted(sig.items()):
        print(f"  {m}  {s:5}  | {r}")


def comparer(slug, date, shadow_dir, jumeau_dir):
    fs = Path(shadow_dir) / slug / f"{date}.ndjson"
    fj = Path(jumeau_dir) / slug / f"{date}.ndjson"
    if not fs.exists():
        print(f"⛔ journal shadow absent : {fs}")
        return
    if not fj.exists():
        print(f"⛔ jumeau du jour absent : {fj}\n   (rejouer le jumeau LEAN sur {date} — exige "
              f"que le CSV 1 m couvre ce jour ; voir la note en tête du script.)")
        return

    evs_s = charger(fs)
    f = fenetres(evs_s)
    sig_s = signaux(evs_s)
    # côté jumeau : ne garder que les signaux DANS les fenêtres où le shadow tournait.
    sig_j = {k: v for k, v in signaux(charger(fj)).items()
             if dans_fenetres(datetime.strptime(k[0], "%Y-%m-%d %H:%M").isoformat(), f)}

    communs = sig_s.keys() & sig_j.keys()
    shadow_seul = sig_s.keys() - sig_j.keys()
    jumeau_seul = sig_j.keys() - sig_s.keys()
    total = len(sig_s | sig_j.keys() if False else (sig_s.keys() | sig_j.keys()))
    print(f"=== PARITÉ PHASE 4 — {slug} {date} ===")
    print(f"Fenêtres shadow : {len(f)} | signaux shadow : {len(sig_s)} | jumeau (dans fenêtres) : {len(sig_j)}")
    print(f"Concordants : {len(communs)} | shadow seul : {len(shadow_seul)} | jumeau seul : {len(jumeau_seul)}")
    if total:
        print(f"Parité décisions : {100*len(communs)/total:.1f}%")
    for m, s in sorted(shadow_seul):
        print(f"  SHADOW SEUL  {m} {s}")
    for m, s in sorted(jumeau_seul):
        print(f"  JUMEAU SEUL  {m} {s}")
    if not shadow_seul and not jumeau_seul and communs:
        print("✅ PARITÉ PARFAITE — mêmes signaux, mêmes minutes, mêmes sens.")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--lister", help="dump des signaux d'un journal ndjson")
    ap.add_argument("--slug")
    ap.add_argument("--date")
    ap.add_argument("--shadow-dir", default=str(SHADOW_DEFAUT))
    ap.add_argument("--jumeau-dir", default=str(JUMEAU_DEFAUT))
    a = ap.parse_args()
    if a.lister:
        lister(a.lister)
    elif a.slug and a.date:
        comparer(a.slug, a.date, a.shadow_dir, a.jumeau_dir)
    else:
        ap.print_help()


if __name__ == "__main__":
    main()
