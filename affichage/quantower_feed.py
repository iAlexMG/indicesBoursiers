"""Client du pont Quantower — flux temps réel NQ (Rithmic) → `FlowStore`.

Pendant Python de la stratégie C# `NqFeed` (`NqFeed/NqFeedStrategy.cs`), qui tourne DANS
Quantower et sert trades + snapshots de carnet en NDJSON sur une socket locale.

Pourquoi un pont et pas une lecture directe : Rithmic n'existe que dans Quantower (le mot de
passe stocké n'est pas déchiffrable hors process — mesuré en Phase 0 du pilier Historique).
Aucun `pythonnet` ne peut charger le BusinessLayer et se connecter seul.

Ce module est l'équivalent d'un connecteur du projet frère (`crypto/affichage/backend/
connectors/*.py`) : la WebSocket d'exchange y devient une socket locale, mais le contrat est le
même — un thread de fond qui ne fait qu'alimenter le store, et une vue qui ignore d'où ça vient.

Ce que le pont garantit (mesuré par la sonde le 2026-07-15, NQ@CME) :
  - le côté agresseur est FOURNI par Rithmic, couverture 100 % → aucune inférence par règle du
    tick, contrairement au flux IBKR (`market_data.py`) ;
  - flux temps réel (retard de cotation annoncé : 0 s) ;
  - pas de `TradeId` → dédup impossible par identifiant (cf. `_seen` plus bas).

Validation du tuyau sans GUI ni IBKR :

    python quantower_feed.py --seconds 15
"""
from __future__ import annotations

import json
import socket
import threading
import time


class QuantowerFeed:
    """Lit le pont et alimente un `FlowStore`. Se reconnecte tout seul.

    Le thread est `daemon` : Quantower peut être fermé, la stratégie arrêtée ou la socket
    coupée sans jamais empêcher l'application de se terminer.
    """

    def __init__(self, store, host: str = "127.0.0.1", port: int = 5555,
                 on_meta=None, log=None) -> None:
        self.store = store
        self.host = host
        self.port = port
        self.on_meta = on_meta          # callback(dict) au « hello » : tick, symbole, exchange…
        self.meta: dict = {}
        self._log = log or (lambda msg: None)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

        # Compteurs — `recv` vs `stored` séparés exprès : `FlowStore.add_book` applique SON
        # propre throttle (`config.SNAPSHOT_MS`) alors que le pont throttle déjà à la source.
        # Deux throttles au même seuil + gigue réseau = snapshots jetés en silence. On mesure
        # l'écart au lieu de le supposer.
        self.n_trades = 0
        self.n_books_recv = 0
        self.n_books_stored = 0
        self.n_bad = 0
        self.connected = False
        self.last_error: str | None = None
        self.quote: tuple = (None, None, None, None)   # (bid, ask, bid_size, ask_size)

    # -- cycle de vie ------------------------------------------------------
    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="quantower-feed", daemon=True)
        self._thread.start()

    def stop(self, timeout: float = 2.0) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=timeout)
            self._thread = None

    # -- boucle ------------------------------------------------------------
    def _run(self) -> None:
        backoff = 1.0
        while not self._stop.is_set():
            try:
                with socket.create_connection((self.host, self.port), timeout=5.0) as sock:
                    sock.settimeout(1.0)          # pour rester réactif au stop
                    self.connected = True
                    self.last_error = None
                    backoff = 1.0
                    self._log(f"pont connecté sur {self.host}:{self.port}")
                    self._read_lines(sock)
            except OSError as exc:
                self.last_error = str(exc)
            finally:
                self.connected = False

            if self._stop.is_set():
                return
            # Reconnexion à backoff plafonné : Quantower est une appli de bureau, elle peut être
            # fermée des heures. On réessaie sans jamais marteler.
            self._log(f"pont indisponible ({self.last_error}) — nouvel essai dans {backoff:.0f} s")
            self._stop.wait(backoff)
            backoff = min(backoff * 2, 30.0)

    def _read_lines(self, sock: socket.socket) -> None:
        buf = b""
        while not self._stop.is_set():
            try:
                chunk = sock.recv(65536)
            except socket.timeout:
                continue
            if not chunk:
                raise OSError("pont fermé par Quantower (stratégie arrêtée ?)")
            buf += chunk
            # NDJSON : on ne traite que les lignes COMPLÈTES, le reliquat attend le paquet suivant.
            *lines, buf = buf.split(b"\n")
            for line in lines:
                if line.strip():
                    self._handle(line)

    def _handle(self, raw: bytes) -> None:
        try:
            msg = json.loads(raw)
            kind = msg.get("t")
            if kind == "trade":
                self._on_trade(msg)
            elif kind == "book":
                self._on_book(msg)
            elif kind == "hello":
                self.meta = msg
                self._log(f"hello : {msg.get('symbol')} @ {msg.get('exchange')} "
                          f"| tick {msg.get('tick')} | {msg.get('levels')} niveaux "
                          f"@ {msg.get('snapshot_ms')} ms")
                if self.on_meta:
                    self.on_meta(msg)
        except Exception as exc:                       # une ligne pourrie ne tue pas le flux
            self.n_bad += 1
            self.last_error = f"{type(exc).__name__}: {exc}"

    def _on_trade(self, msg: dict) -> None:
        # `side` vient de l'AggressorFlag de Rithmic : on le passe explicitement pour que
        # FlowStore n'aille PAS l'inférer. C'est tout l'intérêt de ce flux face à IBKR.
        self.store.add_trade(price=msg["p"], size=msg["s"], side=msg["side"], ts_ms=msg["ts"])
        self.n_trades += 1

    def _on_book(self, msg: dict) -> None:
        self.n_books_recv += 1
        # Top of book du flux de COTATION — indépendant de l'agrégation du carnet. Sert les
        # lignes best bid/ask de la vue, et sert de contre-contrôle : un écart durable avec le
        # 1er niveau du carnet signalerait une agrégation qui perd des niveaux.
        self.quote = (msg.get("qb"), msg.get("qa"), msg.get("qbs"), msg.get("qas"))
        before = len(self.store.books)
        # throttle=False : le pont photographie déjà le carnet à sa propre cadence côté C#.
        # Laisser FlowStore re-throttler au même seuil jetait 29 % des snapshots (mesuré).
        self.store.add_book(bids=msg["b"], asks=msg["a"], ts_ms=msg["ts"], throttle=False)
        if len(self.store.books) != before:
            self.n_books_stored += 1


# --- Validation du tuyau (sans GUI, sans IBKR) -----------------------------

def _main() -> int:
    import argparse

    from orderflow_data import FlowStore

    ap = argparse.ArgumentParser(description="Valide le pont Quantower → FlowStore.")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5555)
    ap.add_argument("--seconds", type=float, default=15.0)
    args = ap.parse_args()

    store = FlowStore("NQ")
    feed = QuantowerFeed(store, host=args.host, port=args.port, log=lambda m: print(f"  {m}"))
    print(f"Connexion au pont {args.host}:{args.port} pendant {args.seconds:.0f} s…")
    print("(la stratégie « NQ-ES RealTime » doit tourner dans Quantower, Rithmic connecté)\n")
    feed.start()
    t0 = time.time()
    try:
        time.sleep(args.seconds)
    except KeyboardInterrupt:
        pass
    feed.stop()
    dt = max(1e-9, time.time() - t0)

    print(f"\n{'=' * 62}\n  VALIDATION DU PONT — {dt:.1f} s\n{'=' * 62}")
    if not feed.n_trades and not feed.n_books_recv:
        print(f"  RIEN REÇU. Dernière erreur : {feed.last_error}")
        print("  → Quantower ouvert ? Rithmic connecté ? stratégie « NQ-ES RealTime » en Working ?")
        return 1

    print(f"  Trades   : {feed.n_trades} ({feed.n_trades / dt:.1f}/s)")
    print(f"  Carnets  : {feed.n_books_recv} reçus ({feed.n_books_recv / dt:.1f}/s), "
          f"{feed.n_books_stored} stockés")
    if feed.n_books_recv and feed.n_books_stored < feed.n_books_recv:
        lost = 100 * (1 - feed.n_books_stored / feed.n_books_recv)
        print(f"  ⚠ {lost:.0f} % des snapshots JETÉS par le throttle de FlowStore "
              f"(config.SNAPSHOT_MS) — il double celui du pont : le baisser.")
    if feed.n_bad:
        print(f"  ⚠ {feed.n_bad} lignes illisibles — dernière : {feed.last_error}")

    trades = list(store.trades)
    if trades:
        buys = sum(1 for t in trades if t.side == "buy")
        print(f"  Agresseur: {buys} achats / {len(trades) - buys} ventes "
              f"(aucune inférence : Rithmic le fournit)")
        print(f"  Dernier  : {trades[-1].price} × {trades[-1].size} ({trades[-1].side})")

    book = store.last_book()
    if book:
        ts, bids, asks = book
        if bids and asks:
            print(f"  Carnet   : {len(bids)} bids × {len(asks)} asks | "
                  f"meilleur {bids[0][0]} / {asks[0][0]} (spread {asks[0][0] - bids[0][0]:.2f})")
    print("=" * 62)
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
