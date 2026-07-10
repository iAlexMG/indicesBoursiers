"""Paramètres globaux du programme.

Modifie ces valeurs pour adapter la connexion, les symboles et l'affichage.
"""

# --- Mode de fonctionnement ------------------------------------------------
# True  = générateur de données synthétiques (aucune connexion IB) -> permet de
#         voir les graphiques footprint/heatmap/DOM tout de suite, même marché
#         fermé ou sans abonnement L2 temps réel.
# False = connexion réelle à TWS/IB Gateway (flux ES & NQ).
DEMO_MODE = True

# --- Connexion IB Gateway / TWS --------------------------------------------
HOST = "127.0.0.1"
PORT = 4001          # ton TWS est configuré sur 4001 | IBG paper = 4002 | TWS paper défaut = 7497
CLIENT_ID = 1        # identifiant unique de la connexion API

# Type de données de marché :
#   1 = temps réel (nécessite un abonnement aux données CME ; requis pour le L2/DOM)
#   3 = différé (delayed, gratuit, ~15 min de retard) -> footprint/scatter OK, DOM vide
#   4 = différé + dernier tick figé (delayed-frozen)
MARKET_DATA_TYPE = 3

# --- Instruments -----------------------------------------------------------
SYMBOLS = ["ES", "NQ"]   # futures E-mini S&P 500 et Nasdaq 100
EXCHANGE = "CME"
CURRENCY = "USD"

# --- Agrégation footprint / bougies ----------------------------------------
RESOLUTION_SECONDS = 5         # durée d'une bougie footprint (s)
MAX_BARS = 200                 # nombre de bougies conservées (borne mémoire)
TRADES_WINDOW_SECONDS = 3600   # rétention des trades en mémoire (1 h)

# --- Carnet d'ordres (Depth of Market) -------------------------------------
DOM_ROWS = 15                  # niveaux bid/ask captés (plus = heatmap/échelle plus parlantes)
SNAPSHOT_MS = 250              # cadence d'enregistrement des snapshots de carnet (heatmap)
BOOKS_WINDOW_SECONDS = 3600    # rétention des snapshots de carnet en mémoire

# --- Interface -------------------------------------------------------------
UPDATE_INTERVAL_MS = 100       # période de rafraîchissement de l'UI (ms, ~10 i/s)
LIVE_SPAN_SECONDS = 180        # largeur initiale de la fenêtre temps (live)
TIMEZONE = "America/Toronto"   # fuseau d'affichage de l'axe temps

# Apparence des graphiques
HEAT_OPACITY = 0.6             # opacité de la heatmap (0..1) -> les trades ressortent
BUY = (0, 220, 130)            # couleur acheteur (vert)
SELL = (240, 80, 80)           # couleur vendeur (rouge)
