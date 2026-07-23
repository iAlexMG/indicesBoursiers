# run_parite.py — PHASE 4 « un bouton » : régénère les données, rejoue les jumeaux LEAN sur
# un jour DÉJÀ PASSÉ, et compare au journal SHADOW live du même jour.
#
# Enchaîne, pour une date cible D :
#   (b) normalize_ohlcv.py --bars-db      -> 1m.csv canonique à jour (barres minute NQ)
#   (c) fenêtre nq_instrument.py réglée sur [D-warmup .. D+2] le temps de rejouer les
#       3 jumeaux LEAN, PUIS restaurée À L'IDENTIQUE (le banc 06-01->07-10 du site est
#       un artefact — jamais laissé modifié, même si LEAN plante : restauration en finally)
#   (d) parite_shadow.comparer(slug, D)   -> concordance des `signal` (minute + sens)
#
# 🪤 LEAN REFUSE DE BACKTESTER LE JOUR COURANT (set_end_date rabattu à hier). Donc D doit
#    être STRICTEMENT ANTÉRIEUR à aujourd'hui : on lance ce script le LENDEMAIN de la séance
#    shadow (ex. séance shadow le 07-23 -> `--date 2026-07-23` lancé le 07-24).
# ⚠️ Pour une parité qui a un sens, le shadow doit être PUR (sans confirmation) et en
#    « Restreindre à la séance NY » RE-COCHÉ — le jumeau LEAN est bridé séance NY.
#
# Usage :
#   python run_parite.py --date 2026-07-23
#   python run_parite.py --date 2026-07-23 --slugs sma_bracket_nq,sma_suiveur_nq
#   python run_parite.py --date 2026-07-23 --warmup-days 5
import argparse
import os
import re
import subprocess
import sys
import tempfile
from datetime import date, datetime, timedelta
from pathlib import Path

import parite_shadow  # même dossier (SHADOW_DEFAUT / JUMEAU_DEFAUT / comparer)

REPO = Path(__file__).resolve().parents[3]                     # …/indicesBoursiers
PORTFOLIO = REPO.parent                                        # …/Portfolio
NORMALIZE = REPO / "historique" / "normalize_ohlcv.py"
ALGO_DIR = REPO / "backtesting" / "backtests" / "algorithms"
INSTRUMENT = ALGO_DIR / "nq_instrument.py"
LAUNCHER = PORTFOLIO / "crypto" / "backtesting" / "backtests" / "lean" / "Launcher" / "bin" / "Release"
CONDA_ENV = Path.home() / "miniconda3" / "envs" / "backtesting"

CSV_DIR = Path(r"H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09")
BARS_DB = Path(r"H:\IndicesBoursiers\historique\NQ-2026-09-1m.db")
CSV_PREFIX = "NQ-CME"

# slug du jumeau -> nom de la classe QCAlgorithm (le --algorithm-type-name de LEAN)
SLUGS = {
    "sma_bracket_nq": "SmaBracketNq",
    "sma_suiveur_nq": "SmaSuiveurNq",
    "sma_annule_nq": "SmaAnnuleNq",
}


def etape(msg):
    print(f"\n=== {msg} ===", flush=True)


def regenerer_csv():
    """(b) 1m.csv canonique depuis la base de barres minute (extracteur NQ-ES History Bars)."""
    etape("(b) régénération du CSV canonique (normalize_ohlcv --bars-db)")
    if not BARS_DB.exists():
        sys.exit(f"⛔ base de barres introuvable : {BARS_DB}\n"
                 f"   Lance l'extracteur « NQ-ES History Bars 1m » (NQ front) dans Quantower.")
    r = subprocess.run([sys.executable, str(NORMALIZE), "--dir", str(CSV_DIR),
                        "--prefix", CSV_PREFIX, "--bars-db", str(BARS_DB)])
    if r.returncode != 0:
        sys.exit("⛔ normalize_ohlcv a échoué.")


def _regler_fenetre(texte, debut, fin):
    """Remplace les 2 lignes FENETRE_* ; renvoie (nouveau_texte, n_remplacements)."""
    t, n1 = re.subn(r"^FENETRE_DEBUT = datetime\([^)]*\)",
                    f"FENETRE_DEBUT = datetime({debut.year}, {debut.month}, {debut.day})",
                    texte, flags=re.M)
    t, n2 = re.subn(r"^FENETRE_FIN = datetime\([^)]*\)",
                    f"FENETRE_FIN = datetime({fin.year}, {fin.month}, {fin.day})",
                    t, flags=re.M)
    return t, n1 + n2


def rejouer_jumeaux(cible, slugs, warmup_days):
    """(c) fenêtre temporaire [cible-warmup .. cible+2], rejeu LEAN, restauration en finally."""
    etape(f"(c) rejeu des jumeaux LEAN pour {cible} (fenêtre temporaire, restaurée ensuite)")
    debut = cible - timedelta(days=warmup_days)
    fin = cible + timedelta(days=2)   # borne haute ; LEAN plafonne lui-même à hier + CSV

    original = INSTRUMENT.read_text(encoding="utf-8")
    modifie, n = _regler_fenetre(original, debut, fin)
    if n != 2:
        sys.exit(f"⛔ impossible de localiser les 2 lignes FENETRE_* dans {INSTRUMENT} "
                 f"({n}/2 trouvées) — rien n'a été modifié. Vérifie le fichier à la main.")

    if not (LAUNCHER / "QuantConnect.Lean.Launcher.dll").exists():
        sys.exit(f"⛔ Launcher LEAN introuvable : {LAUNCHER}")
    dll = CONDA_ENV / "python311.dll"
    if not dll.exists():
        sys.exit(f"⛔ python311.dll de l'env conda backtesting introuvable : {dll}")

    env = dict(os.environ, PYTHONNET_PYDLL=str(dll), PYTHONHOME=str(CONDA_ENV))
    sortie = Path(tempfile.mkdtemp(prefix="parite_lean_"))
    print(f"  fenêtre {debut:%Y-%m-%d} -> {fin:%Y-%m-%d} | sortie LEAN : {sortie}")

    INSTRUMENT.write_text(modifie, encoding="utf-8")
    try:
        for slug in slugs:
            classe = SLUGS[slug]
            print(f"  -- LEAN {classe} ({slug}) …", flush=True)
            log = sortie / f"{classe}.out.txt"
            with open(log, "w", encoding="utf-8", errors="replace") as fo:
                subprocess.run(
                    ["dotnet", "QuantConnect.Lean.Launcher.dll", "--algorithm-language", "Python",
                     "--algorithm-type-name", classe,
                     "--algorithm-location", str(ALGO_DIR / f"{slug}.py"),
                     "--results-destination-folder", str(sortie), "--close-automatically", "true"],
                    cwd=str(LAUNCHER), env=env, stdout=fo, stderr=subprocess.STDOUT)
            # LEAN sort en code 82 / crash GILState cosmétique : le juge est le BILAN.
            for ligne in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if "BILAN" in ligne or "Entrées" in ligne or "Dates: Start" in ligne:
                    print("     " + ligne.split("TRACE:: ")[-1].strip())
    finally:
        INSTRUMENT.write_text(original, encoding="utf-8")
        print("  ✅ fenêtre nq_instrument.py restaurée à l'identique.")


def comparer(cible, slugs):
    """(d) parité shadow live <-> jumeau backtest, par stratégie."""
    etape(f"(d) parité SHADOW ↔ jumeau pour {cible}")
    d = cible.isoformat()
    for slug in slugs:
        print()
        parite_shadow.comparer(slug, d, str(parite_shadow.SHADOW_DEFAUT),
                               str(parite_shadow.JUMEAU_DEFAUT))


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--date", required=True, help="jour cible ET (YYYY-MM-DD), déjà PASSÉ")
    ap.add_argument("--slugs", default=",".join(SLUGS),
                    help="stratégies (défaut : les 3)")
    ap.add_argument("--warmup-days", type=int, default=4,
                    help="jours d'amorçage des indicateurs avant la cible (défaut 4)")
    ap.add_argument("--skip-csv", action="store_true", help="ne pas régénérer le CSV")
    a = ap.parse_args()

    try:
        cible = datetime.strptime(a.date, "%Y-%m-%d").date()
    except ValueError:
        sys.exit("⛔ --date doit être au format YYYY-MM-DD.")
    if cible >= date.today():
        sys.exit(f"⛔ {cible} n'est pas dans le passé. LEAN refuse de backtester aujourd'hui "
                 f"ou le futur — lance ce script le LENDEMAIN de la séance shadow.")

    slugs = [s.strip() for s in a.slugs.split(",") if s.strip()]
    inconnus = [s for s in slugs if s not in SLUGS]
    if inconnus:
        sys.exit(f"⛔ slug(s) inconnu(s) : {inconnus}. Connus : {list(SLUGS)}")

    print(f"PHASE 4 — parité pour {cible} | stratégies : {slugs}")
    if not a.skip_csv:
        regenerer_csv()
    rejouer_jumeaux(cible, slugs, a.warmup_days)
    comparer(cible, slugs)
    print("\n✅ Terminé. (Un « shadow seul » hors 09:30-15:30 ET = config 24 h côté shadow, "
          "pas une vraie divergence — voir le README.)")


if __name__ == "__main__":
    main()
