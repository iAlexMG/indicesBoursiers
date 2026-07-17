"""Paramètres globaux du programme.

Modifie ces valeurs pour adapter la connexion, les symboles et l'affichage.
"""

# --- Accès aux données -----------------------------------------------------
# Les TROIS accès tournent EN PARALLÈLE (main.py les monte tous) : le menu « Accès » de la
# barre ne change que la source LUE, sans rien redémarrer -> aucun trou de données à la
# bascule. Un accès en panne (pont fermé, TWS éteint) laisse sa vue vide, sans gêner les
# autres. L'ordre de cette table est celui du menu.
#
# "quantower" : le pont `NqFeed` (Rithmic), via la stratégie qui tourne DANS Quantower.
#               Carnet L2 complet et côté agresseur FOURNI par le marché (couverture
#               mesurée : 100 %) -> les 4 couches s'affichent, le footprint est exact.
# "ibkr"      : TWS / IB Gateway. Mode DÉGRADÉ ici : sans abonnement CME L2, la heatmap
#               et le DOM restent vides et le côté agresseur doit être INFÉRÉ (règle du
#               tick). Conservé pour ce qu'il démontre : deux accès au même marché.
#
# ⚠️ La DÉMO n'est PLUS dans le menu (choix de l'utilisateur, 2026-07-15). Elle reste un
# outil de DÉVELOPPEMENT : `demo_feed.py` alimente les tests headless et permet de valider
# l'affichage hors séance (le CME est fermé ~76 h par semaine). Sa place n'était pas à
# l'écran : un tableau de bord dont l'argument est « d'où vient la donnée » ne doit pas
# proposer des chiffres INVENTÉS à côté des vrais. Pour la remettre le temps d'un essai,
# ajouter ("demo", "Démo") ci-dessous — `BUILDERS` la connaît toujours.
ACCESS = [("quantower", "Quantower"), ("ibkr", "IBKR")]

# Accès SÉLECTIONNÉ au démarrage (les trois n'en tournent pas moins). Doit figurer dans
# ACCESS ci-dessus.
SOURCE = "quantower"

# --- Pont Quantower (SOURCE = "quantower") ---------------------------------
# UNE instance de la stratégie « NQ-ES RealTime » PAR symbole, chacune sur son propre port
# (paramètre « Port TCP » de la stratégie, côté Quantower). Un symbole absent de cette
# table n'a pas de flux : son onglet reste vide, sans empêcher les autres de tourner.
QT_FEED_HOST = "127.0.0.1"
QT_FEED_PORTS = {"NQ": 5555, "ES": 5556}

# --- Connexion IB Gateway / TWS (accès "ibkr") ------------------------------
# ⚠️ LE PORT DÉPEND DE CE QUI TOURNE — c'est la 1re chose à vérifier devant un IBKR muet :
#   7497 = TWS      paper (défaut)      7496 = TWS         live
#   4002 = Gateway  paper (défaut)      4001 = IB Gateway  live
# Mesuré le 2026-07-15 : seul 7497 écoutait -> TWS en paper. (La valeur d'avant, 4001, visait
# une installation Gateway qui n'existe plus.) Pour trouver le bon port sans deviner :
#   foreach ($p in 4001,4002,7496,7497) { Test-NetConnection 127.0.0.1 -Port $p }
# TWS n'ouvre ce port QUE si l'API est activée (Configure > API > Settings > Enable ActiveX
# and Socket Clients) : un port ouvert prouve donc déjà que l'API est en service.
HOST = "127.0.0.1"
PORT = 7497
CLIENT_ID = 1        # identifiant unique de la connexion API

# Type de données de marché :
#   1 = temps réel (nécessite un abonnement CME)   3 = différé si pas d'abonnement (~15 min)
#   2 = figé                                       4 = différé + figé
#
# ⚠️ CE COMPTE EST EN DIFFÉRÉ — REMESURÉ le 2026-07-16, par QUATRE chemins indépendants :
#   1. `ticker.marketDataType` renvoyé par TWS  -> 3 (différé)
#   2. `Warning 10167` : « données de marché en différé affichées »
#   3. `Error 10189` sur reqTickByTickData      -> « No market data permissions for CME FUT »
#   4. le MÊME contrat NQ lu au même instant chez Rithmic : l'écart DÉRIVE (-2,50 -> -12,25
#      pts en 30 s). Deux flux temps réel colleraient à ±1 tick de façon stable.
#
# 🪤 LA MESURE QUI DISAIT « TEMPS RÉEL, 1,8 s » ÉTAIT CIRCULAIRE — ne pas la refaire.
# Elle lisait `ticker.time` en croyant y trouver l'horodatage du marché. Or ib_insync fait
# `ticker.time = self.lastTime` avec `self.lastTime = datetime.now(timezone.utc)` : c'est
# l'heure de RÉCEPTION. Elle rend donc ~2 s quoi qu'il arrive, différé compris. Le piège est
# le même que celui de `FlowStore` (qui horodate aussi à la réception), juste un cran plus
# bas : AUCUN champ d'ib_insync ne donne l'heure du marché sans `genericTickList` "233"
# (RTVolume), lui-même réservé aux abonnés. Pour trancher : demander à TWS, ou comparer à
# une source INDÉPENDANTE (le pont Rithmic) — jamais interroger le flux sur lui-même.
# ✅ LE RETARD EST MESURÉ : **11,5 min** (2026-07-16, NQ). Corrélation croisée entre les deux
# accès dans `trades.db` : écart médian minimal (1,53 pt) quand on recale IBKR de 11,5 min,
# contre 12,50 pts à lag 0. Les données IBKR ne sont donc pas FAUSSES, elles sont VIEILLES —
# mêmes prix à 1,5 pt près, 11,5 min plus tard. ⚠️ Ce n'est PAS le « ~15 min » de la
# convention IBKR : citer « ~11-12 min, mesuré », jamais le chiffre de la brochure.
#
# 🪤 SECOND DÉFAUT, INDÉPENDANT DU RETARD : LA COUVERTURE. `reqMktData` ne diffuse pas chaque
# transaction, il publie l'état du dernier prix par intervalles. MESURÉ sur NQ : **0,3 mise
# à jour/s contre 9,0 trades/s** servis par Rithmic sur le même contrat, soit ~3 % des
# transactions -> le VOLUME du footprint IBKR est faux d'un facteur ~30, en plus d'un
# agresseur inféré. `reqTickByTickData("AllLast")` serait la sortie, mais elle est FERMÉE
# ici (erreur 10189 ci-dessus) : c'est un achat d'abonnement, pas du développement.
#
# Laisser à 3 : c'est le réglage qui marche AVEC et SANS abonnement.
MARKET_DATA_TYPE = 3

# --- Instruments -----------------------------------------------------------
SYMBOLS = ["ES", "NQ"]   # futures E-mini S&P 500 et Nasdaq 100
EXCHANGE = "CME"
CURRENCY = "USD"

# --- Agrégation footprint / bougies ----------------------------------------
RESOLUTION_SECONDS = 5         # durée d'une bougie footprint (s)
MAX_BARS = 200                 # nombre de bougies conservées (borne mémoire)
TRADES_WINDOW_SECONDS = 3600   # rétention des trades en mémoire (1 h)

# --- Persistance disque ----------------------------------------------------
# Le carnet n'a AUCUN historique téléchargeable (Rithmic ne sert pas de L2 passé, et aucun
# exchange n'en sert non plus côté crypto) : la seule façon d'avoir une heatmap historique
# est d'enregistrer le live au fil de l'eau. L'historique ne grandit donc que vers l'avant,
# pendant les sessions où l'app tourne. Bases supprimables app fermée (elles se recréent).
DATA_DIR = "data"          # trades.db + books.db (+ WAL). Sur ce poste "data" est une
                           # jonction NTFS -> H:\indices-affichage (les .db vivent sur H:).
RETENTION_DAYS = 7.0       # purge périodique au-delà -> les bases ne gonflent pas sans fin

# Cadence d'ENREGISTREMENT des snapshots, DÉCOUPLÉE de celle de l'affichage (SNAPSHOT_MS).
# Les deux besoins n'ont rien à voir : l'écran veut du 250 ms pour être fluide sur 3 minutes,
# le disque ne sert qu'à l'historique au-delà de la fenêtre mémoire — où la heatmap agrège de
# toute façon à ~1000 colonnes étalées sur des heures. Enregistrer à 250 ms y serait invisible
# et coûterait 4× le disque. MESURÉ : un snapshot 100×100 pèse ~4,4 ko, donc 1,54 Go/JOUR à
# 250 ms (10,8 Go sur 7 jours) contre 385 Mo/jour à 1 s. Le frère crypto enregistre à 1 Hz
# pour la même raison. Ne pas descendre sous SNAPSHOT_MS : ce serait sans effet.
RECORD_SNAPSHOT_MS = 1000

# --- Carnet d'ordres (Depth of Market) -------------------------------------
DOM_ROWS = 15                  # niveaux bid/ask captés côté IBKR (reqMktDepth)
SNAPSHOT_MS = 250              # cadence d'enregistrement des snapshots de carnet (heatmap)
BOOKS_WINDOW_SECONDS = 3600    # rétention des snapshots de carnet en mémoire

# --- Granularité de l'affichage (l'arbitrage qui compte) --------------------
# Ces deux réglages décident, ENSEMBLE, de la finesse du footprint. Le lien n'est pas
# évident, alors voici la mécanique — elle a été MESURÉE, pas supposée :
#
# Le footprint choisit son pas de regroupement pour tenir en FOOTPRINT_MAX_ROWS lignes dans
# l'étendue de prix affichée. Donc plus la vue est large, plus il agrège. Or l'axe des prix
# englobait TOUTE la profondeur servie par le pont (100 niveaux/côté = 50 points sur le NQ),
# ce qui forçait **10 ticks par ligne de footprint** alors que le DOM en montre 1.
#
#   étendue de l'axe   ->  ligne de footprint (NQ, tick 0,25)
#     50 pts (100 niv.)  ->  2,50  = 10 ticks   <- l'ancien comportement
#     10 pts ( 20 niv.)  ->  0,50  =  2 ticks
#      5 pts ( 10 niv.)  ->  0,25  =  1 tick    = exactement le DOM
#
# ⚠️ Relever FOOTPRINT_MAX_ROWS seul ne suffit PAS : de 46 à 300, on ne gagne que 10 -> 2
# ticks (le pas se cale sur des valeurs rondes). C'est VIEW_LEVELS qui a le plus d'effet.
#
# L'ARBITRAGE, à trancher selon ce qu'on veut lire : une vue étroite donne un footprint fin
# mais une heatmap peu profonde (les « murs » lointains sortent de l'écran) ; une vue large
# fait l'inverse. Ctrl+molette zoome les prix à tout moment — le footprint suit.
VIEW_LEVELS = 20               # niveaux de carnet de chaque côté englobés par l'axe des prix
                               # (le pont en sert 100 : la heatmap les a, l'écran n'en montre
                               #  qu'une bande. L'axe s'élargit quand même pour couvrir les
                               #  trades visibles.)
FOOTPRINT_MAX_ROWS = 120       # lignes de prix max du footprint avant qu'il ne regroupe
                               # ⚠️ au-delà de ~75 lignes sur un écran 900 px, les chiffres
                               # bid×ask s'effacent tout seuls (plus la place) — les barres,
                               # elles, restent. C'est le prix d'un footprint au tick.

# --- Détection d'un flux MORT ----------------------------------------------
# Au-delà de ce délai SANS que le contenu du carnet change — alors que les photos, elles,
# continuent d'arriver —, l'écran annonce un FLUX GELÉ.
#
# 🪤 LE PIÈGE QUE ÇA COUVRE (vécu le 2026-07-16, en pleine séance) : quand Rithmic cesse
# d'alimenter Quantower, la stratégie continue de photographier le carnet toutes les 250 ms.
# Les photos arrivent donc fraîches, à 4/s... et RIGOUREUSEMENT IDENTIQUES. Mesuré : 80
# photos, 1 seule valeur distincte, 0 trade. Comme `t_last` ne mesure que l'ARRIVÉE, l'app
# affichait « ● reçu il y a 0,2 s » sur un carnet mort depuis 20 minutes — un écran faux qui
# se présentait comme sain. Seul le CONTENU trahit la panne (`FlowStore.t_book_change`).
#
# 30 s est volontairement large : le carnet du NQ (100 niveaux/côté) bouge en permanence,
# même la nuit — un gel de 30 s n'est jamais normal. Ne pas trop descendre : hors séance, un
# marché vraiment calme pourrait faire un faux positif, et une fausse alerte coûte la
# confiance qu'on essaie justement de bâtir.
STALE_BOOK_SECONDS = 30.0

# --- Interface -------------------------------------------------------------
UPDATE_INTERVAL_MS = 100       # période de rafraîchissement de l'UI (ms, ~10 i/s)
LIVE_SPAN_SECONDS = 180        # largeur initiale de la fenêtre temps (live)
TIMEZONE = "America/Toronto"   # fuseau d'affichage de l'axe temps

# Apparence des graphiques
HEAT_OPACITY = 0.6             # opacité de la heatmap (0..1) -> les trades ressortent
BUY = (0, 220, 130)            # couleur acheteur (vert)
SELL = (240, 80, 80)           # couleur vendeur (rouge)
