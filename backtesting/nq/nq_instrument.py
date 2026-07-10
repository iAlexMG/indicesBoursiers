# Module d'instrument NQ partagé par les backtests NQ (Phase 2 du projet Quantower).
# Regroupe ce qui DIFFÈRE du monde crypto : lecteur de données NQ, FeeModel par contrat,
# SymbolProperties futures (tick 0,25 / multiplicateur 20 / lot 1), effet de levier (marge).
# La LOGIQUE des stratégies (SMA/MACD/RSI…) reste identique au projet frère.
from AlgorithmImports import *
from datetime import datetime, timedelta

# Source de vérité NQ produite par l'extracteur Quantower + normalize_ohlcv.py.
DATA_FILE = "F:/data/ohlcv/NQ-2026-09/1H.csv"
CAPITAL = 100_000

# Spécifications mesurées en Phase 0 (Phase0 Measure) :
TICK_SIZE = 0.25
MULTIPLIER = 20                 # 5 $/tick ÷ 0,25 = 20 $/point
# Frais : commission FIXE par contrat et par side. ~4 $ aller-retour sur NQ = ~2 $/side.
# ⚠️ ordre de grandeur — à REMPLACER par la valeur réelle du plan Rithmic (à mesurer).
COMMISSION_PAR_CONTRAT = 2.0
# Futures = marge, pas comptant : 1 contrat ≈ 600 k$ de notionnel > 100 k$ de capital.
# Levier généreux pour autoriser la tenue de contrats sur 100 k$ (SIMPLIFICATION ; la marge
# prop firm réelle et son trailing drawdown seront modélisés au risk manager de la Phase 5).
LEVERAGE = 20


class NqHourly(PythonData):
    """Lecteur custom : une ligne du CSV canonique -> une barre LEAN (identique au frère)."""

    def get_source(self, config, date, is_live):
        return SubscriptionDataSource(DATA_FILE, SubscriptionTransportMedium.LOCAL_FILE)

    def reader(self, config, line, date, is_live):
        if not line or not line[0].isdigit():
            return None
        cols = line.split(",")
        bar = NqHourly()
        bar.symbol = config.symbol
        t_open = datetime.strptime(cols[0][:19], "%Y-%m-%d %H:%M:%S")
        bar.time = t_open
        bar.end_time = t_open + timedelta(hours=1)
        bar.value = float(cols[4])
        bar["open"] = float(cols[1])
        bar["high"] = float(cols[2])
        bar["low"] = float(cols[3])
        bar["close"] = float(cols[4])
        bar["volume"] = float(cols[5])
        return bar


class NqFeeModel(FeeModel):
    """Commission par contrat et par side (remplace le 0,04 % du notionnel crypto)."""

    def get_order_fee(self, parameters):
        contrats = abs(parameters.order.quantity)
        return OrderFee(CashAmount(contrats * COMMISSION_PAR_CONTRAT, "USD"))


def dates_from_csv():
    """Bornes adaptatives lues dans le CSV (jamais de dates figées)."""
    with open(DATA_FILE) as f:
        rows = f.read().splitlines()
    premier = datetime.strptime(rows[1][:19], "%Y-%m-%d %H:%M:%S")
    dernier = datetime.strptime(rows[-1][:19], "%Y-%m-%d %H:%M:%S")
    return premier, dernier


def setup_nq(algo):
    """Configure l'algo pour NQ et renvoie le Symbol. À appeler dans initialize()."""
    premier, dernier = dates_from_csv()
    algo.set_start_date(premier.year, premier.month, premier.day)
    algo.set_end_date(dernier.year, dernier.month, dernier.day)
    algo.set_cash(CAPITAL)
    algo.set_time_zone(TimeZones.UTC)

    proprietes = SymbolProperties("NQ E-mini Nasdaq-100 CME", "USD",
                                  MULTIPLIER, TICK_SIZE, 1, "NQ")
    heures = SecurityExchangeHours.always_open(TimeZones.UTC)
    securite = algo.add_data(NqHourly, "NQ", proprietes, heures, Resolution.HOUR)
    securite.set_fee_model(NqFeeModel())
    securite.set_leverage(LEVERAGE)
    return securite.symbol
