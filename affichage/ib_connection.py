"""Gestion de la connexion à IB Gateway et résolution des contrats futures."""

import logging
from datetime import datetime

from ib_insync import IB, Future

import config

log = logging.getLogger("ibkr")


def connect():
    """Ouvre la connexion à IB Gateway/TWS et configure le type de données.

    `readonly=True` : ib_insync ne tente pas de lire les ordres au démarrage,
    ce qui évite les erreurs 321 quand l'API TWS est en mode lecture seule.
    """
    ib = IB()
    ib.connect(config.HOST, config.PORT, clientId=config.CLIENT_ID, readonly=True)
    ib.reqMarketDataType(config.MARKET_DATA_TYPE)
    log.info("connecté à IB Gateway %s:%s (clientId=%s)",
             config.HOST, config.PORT, config.CLIENT_ID)

    # Simple notification (pas de reconnexion ici : appeler connect() depuis
    # l'event loop provoque une ré-entrance et fait planter l'UI). La reconnexion
    # est le travail de la boucle d'`IbkrFeed`, dans son thread.
    ib.disconnectedEvent += lambda: log.info("déconnecté d'IB Gateway.")
    return ib


def _expiry_key(contract):
    """Clé de tri/comparaison robuste pour l'échéance (gère YYYYMM et YYYYMMDD)."""
    s = contract.lastTradeDateOrContractMonth
    return s if len(s) == 8 else s + "31"


def resolve_front_month(ib, symbol):
    """Retourne le contrat future front-month (échéance la plus proche non expirée)."""
    template = Future(symbol, exchange=config.EXCHANGE, currency=config.CURRENCY)
    details = ib.reqContractDetails(template)
    if not details:
        raise RuntimeError(f"Aucun contrat trouvé pour {symbol} sur {config.EXCHANGE}.")

    contracts = sorted((d.contract for d in details), key=_expiry_key)
    today = datetime.now().strftime("%Y%m%d")

    front = next((c for c in contracts if _expiry_key(c) >= today), contracts[-1])
    ib.qualifyContracts(front)
    log.info("%s -> contrat %s (échéance %s)",
             symbol, front.localSymbol, front.lastTradeDateOrContractMonth)
    return front
