"""Figures « signal » des fiches NQ : le prix, l'indicateur de la stratégie et
les VRAIES entrées/sorties (les fills LEAN), sur une fenêtre zoomée choisie là
où les trades sont les plus denses.

Complément des courbes d'équité (fig_equite_nq.py) : là où l'équité montre le
résultat, le signal montre la DÉCISION — ce que le user demandait (« un chart
des données + l'indicateur, avec des marqueurs »). Rien n'est figé : le prix
vient du 1m.csv canonique, l'indicateur est recalculé aux mêmes paramètres que
l'algo, et les marqueurs sont relus des order-events écrits par LEAN.

    python backtests/fig_signal_nq.py --runs <dossier des runs LEAN> \
           --out <iAlexMG.ca>/assets/indices/backtesting/figures

Le dossier des runs contient un sous-dossier par stratégie (buyhold,
sma_croisement_nq, …), chacun avec son <Classe>-order-events.json (fills).
"""
import argparse
import json
from datetime import timezone
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import numpy as np
import pandas as pd

ENCRE, BLEU, ROUGE, AMBRE, GRIS = "#263238", "#1d4ed8", "#c62828", "#b45309", "#78909c"
VERT = "#15803d"
DATA_FILE = "H:/IndicesBoursiers/historique/ohlcv/NQ-2026-09/1m.csv"
VP_FILE = "H:/IndicesBoursiers/historique/ohlcv/NQ-2026-09/features_vp.csv"
FENETRE = ("2026-06-01", "2026-07-10")   # la fenêtre du banc (nq_instrument)

# (slug figure, dossier du run, classe LEAN, titre, tf signal en min, genre, params)
STRATS = [
    ("buyhold", "buyhold", "BuyHoldNq", "Buy & Hold NQ (1 contrat)", None, "hold", {}),
    ("sma", "sma_croisement_nq", "SmaCroisementNq", "Croisement SMA 9/21 — NQ, barres 15 m",
     15, "sma", dict(rapide=9, lente=21, regime=48)),
    ("macd", "macd_nq", "MacdNq", "MACD 12/26/9 — NQ, barres 15 m",
     15, "macd", dict(rapide=12, lente=26, signal=9)),
    ("rsi", "rsi_retour_moyenne_nq", "RsiRetourMoyenneNq", "RSI 9 (30/70) — NQ, barres 3 m",
     3, "rsi", dict(periode=9, bas=30, haut=70)),
    ("bollinger", "bollinger_nq", "BollingerNq", "Bandes de Bollinger 20 / 2σ — NQ, barres 5 m",
     5, "bollinger", dict(periode=20, k=2.0)),
    ("risque", "risque_stops_nq", "RisqueStopsNq", "RSI 9 + stops ATR — NQ, barres 3 m",
     3, "rsi", dict(periode=9, bas=30, haut=70)),
    ("avancee", "strategie_avancee_nq", "StrategieAvanceeNq",
     "Stratégie avancée (régime + momentum) — NQ, barres 15 m",
     15, "avancee", dict(regime=48, periode=9)),
    ("vp", "volume_profile_nq", "VolumeProfileNq", "Volume profile par session — NQ, barres 1 m",
     1, "vp", {}),
]


def charger_prix():
    df = pd.read_csv(DATA_FILE, usecols=["time", "close"], parse_dates=["time"])
    df = df.set_index("time").sort_index()
    df = df.loc[FENETRE[0]:FENETRE[1]]
    if df.index.tz is None:
        df.index = df.index.tz_localize(timezone.utc)
    return df["close"]


def rees(close, tf):
    """Rééchantillonne comme l'algo : le close 1 m aux minutes multiples de tf."""
    if tf is None or tf <= 1:
        return close
    return close[close.index.minute % tf == 0]


def rsi_wilders(s, n):
    d = s.diff()
    gain = d.clip(lower=0.0)
    perte = -d.clip(upper=0.0)
    ag = gain.ewm(alpha=1.0 / n, adjust=False).mean()
    ap = perte.ewm(alpha=1.0 / n, adjust=False).mean()
    rs = ag / ap.replace(0.0, np.nan)
    return 100.0 - 100.0 / (1.0 + rs)


def fills(runs, dossier, cls):
    """Entrées/sorties réellement exécutées : (temps, prix, sens) des fills."""
    p = Path(runs) / dossier / f"{cls}-order-events.json"
    ev = json.load(open(p, encoding="utf-8"))
    out = []
    for e in ev:
        if e.get("status") != "filled":
            continue
        px = float(e.get("fillPrice") or 0.0)
        if px <= 0:
            continue
        t = pd.Timestamp(e["time"], unit="s", tz="UTC")
        out.append((t, px, e.get("direction", "")))
    return pd.DataFrame(out, columns=["t", "px", "sens"])


def zoom(fdf, close, jours=3):
    """Fenêtre de `jours` jours qui contient le plus de fills (sinon début de banc)."""
    if fdf.empty:
        deb = close.index[0]
        return deb, deb + pd.Timedelta(days=jours)
    largeur = pd.Timedelta(days=jours)
    meilleur, best_n = fdf["t"].iloc[0], 0
    for t0 in fdf["t"]:
        n = ((fdf["t"] >= t0) & (fdf["t"] < t0 + largeur)).sum()
        if n > best_n:
            best_n, meilleur = n, t0
    deb = meilleur - pd.Timedelta(hours=8)
    return deb, deb + largeur + pd.Timedelta(hours=16)


def marqueurs(ax, fdf, d0, d1):
    m = fdf[(fdf["t"] >= d0) & (fdf["t"] <= d1)]
    ach = m[m["sens"] == "buy"]
    ven = m[m["sens"] == "sell"]
    ax.scatter(ach["t"], ach["px"], marker="^", s=70, c=VERT, zorder=5,
               edgecolors="white", linewidths=0.6, label="Achat (fill)")
    ax.scatter(ven["t"], ven["px"], marker="v", s=70, c=ROUGE, zorder=5,
               edgecolors="white", linewidths=0.6, label="Vente (fill)")


def habiller(fig, ax, titre):
    ax.set_title(titre, color=ENCRE, fontsize=12, fontweight="bold", loc="left", pad=10)
    for a in fig.axes:
        a.grid(True, color=GRIS, alpha=0.18, linewidth=0.6)
        a.xaxis.set_major_formatter(mdates.DateFormatter("%d %b"))
        for sp in a.spines.values():
            sp.set_color(GRIS)
            sp.set_alpha(0.4)
        a.tick_params(colors=ENCRE, labelsize=8)
    ax.legend(loc="upper left", fontsize=8, framealpha=0.9, facecolor="white")


def tracer(slug, dossier, cls, titre, tf, genre, params, close, runs, out):
    fdf = fills(runs, dossier, cls)
    d0, d1 = (close.index[0], close.index[-1]) if genre == "hold" else zoom(fdf, close)
    px = close[(close.index >= d0) & (close.index <= d1)]

    def w(s):   # tronque une série au zoom, pour que X et Y ne s'étirent pas
        return s[(s.index >= d0) & (s.index <= d1)]

    hauteur = 2 if genre in ("macd", "rsi") else 1
    fig, axes = plt.subplots(hauteur, 1, figsize=(11, 5.2 if hauteur == 2 else 4.4),
                             sharex=True, gridspec_kw=dict(height_ratios=[3, 1] if hauteur == 2 else [1]),
                             dpi=140)
    ax = axes[0] if hauteur == 2 else axes
    ax.plot(px.index, px.values, color=ENCRE, lw=0.9, alpha=0.85, label="NQ (close 1 m)")

    if genre == "sma":
        r = w(rees(close, tf).rolling(params["rapide"]).mean())
        l = w(rees(close, tf).rolling(params["lente"]).mean())
        ax.plot(r.index, r.values, color=BLEU, lw=1.4, label=f"SMA {params['rapide']}")
        ax.plot(l.index, l.values, color=AMBRE, lw=1.4, label=f"SMA {params['lente']}")
    elif genre == "bollinger":
        base = rees(close, tf)
        mid = base.rolling(params["periode"]).mean()
        sd = base.rolling(params["periode"]).std(ddof=0)
        up, lo, mid = w(mid + params["k"] * sd), w(mid - params["k"] * sd), w(mid)
        ax.plot(mid.index, mid.values, color=BLEU, lw=1.2, label=f"SMA {params['periode']}")
        ax.plot(up.index, up.values, color=GRIS, lw=1.0, ls="--", label=f"± {params['k']:g}σ")
        ax.plot(lo.index, lo.values, color=GRIS, lw=1.0, ls="--")
    elif genre == "avancee":
        reg = w(rees(close, tf).rolling(params["regime"]).mean())
        ax.plot(reg.index, reg.values, color=BLEU, lw=1.4, label=f"SMA régime {params['regime']}")
    elif genre == "vp":
        vp = pd.read_csv(VP_FILE, parse_dates=["time"]) if Path(VP_FILE).exists() else None
        if vp is not None:
            vp = vp.set_index("time")
            if vp.index.tz is None:
                vp.index = vp.index.tz_localize(timezone.utc)
            vp = vp[(vp.index >= d0) & (vp.index <= d1)]
            for col, coul, lab in [("poc", ROUGE, "POC"), ("vah", GRIS, "VAH"), ("val", GRIS, "VAL")]:
                if col in vp.columns:
                    ax.plot(vp.index, vp[col].values, color=coul, lw=1.0,
                            ls="-" if col == "poc" else ":", label=lab if col != "val" else None)

    marqueurs(ax, fdf, d0, d1)
    ax.set_ylabel("Prix NQ", color=ENCRE, fontsize=9)

    if genre == "macd":
        e_r = rees(close, tf).ewm(span=params["rapide"], adjust=False).mean()
        e_l = rees(close, tf).ewm(span=params["lente"], adjust=False).mean()
        macd = e_r - e_l
        sgn = macd.ewm(span=params["signal"], adjust=False).mean()
        hist = macd - sgn
        w = macd[(macd.index >= d0) & (macd.index <= d1)]
        s2 = sgn[(sgn.index >= d0) & (sgn.index <= d1)]
        h2 = hist[(hist.index >= d0) & (hist.index <= d1)]
        axes[1].bar(h2.index, h2.values, width=0.006, color=[VERT if v >= 0 else ROUGE for v in h2.values], alpha=0.5)
        axes[1].plot(w.index, w.values, color=BLEU, lw=1.1, label="MACD")
        axes[1].plot(s2.index, s2.values, color=AMBRE, lw=1.1, label="Signal")
        axes[1].axhline(0, color=GRIS, lw=0.8, alpha=0.6)
        axes[1].set_ylabel("MACD", color=ENCRE, fontsize=9)
        axes[1].legend(loc="upper left", fontsize=7, framealpha=0.9)
    elif genre == "rsi":
        rsi = rsi_wilders(rees(close, tf), params["periode"])
        r2 = rsi[(rsi.index >= d0) & (rsi.index <= d1)]
        axes[1].plot(r2.index, r2.values, color=BLEU, lw=1.1, label=f"RSI {params['periode']}")
        axes[1].axhline(params["haut"], color=ROUGE, lw=0.8, ls="--", alpha=0.7)
        axes[1].axhline(params["bas"], color=VERT, lw=0.8, ls="--", alpha=0.7)
        axes[1].set_ylim(0, 100)
        axes[1].set_ylabel("RSI", color=ENCRE, fontsize=9)
        axes[1].legend(loc="upper left", fontsize=7, framealpha=0.9)

    ax.set_xlim(d0, d1)
    habiller(fig, ax, titre)
    fig.autofmt_xdate()
    fig.tight_layout()
    cible = Path(out) / f"signal-{slug}.png"
    fig.savefig(cible, facecolor="white", bbox_inches="tight")
    plt.close(fig)
    return cible, len(fdf)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--runs", required=True, help="dossier des runs LEAN")
    ap.add_argument("--out", required=True, help="dossier des PNG (assets du site)")
    args = ap.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    close = charger_prix()
    for slug, dossier, cls, titre, tf, genre, params in STRATS:
        cible, n = tracer(slug, dossier, cls, titre, tf, genre, params, close, args.runs, out)
        print(f"  signal-{slug}.png  ({n} fills)  -> {cible}")


if __name__ == "__main__":
    main()
