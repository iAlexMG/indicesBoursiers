# Règles Apex sur l'automatisation et les bots — source de référence

> Écrite le 2026-07-22, à ta demande, pour figer la compréhension des LIMITES du règlement
> Apex qui encadre les modes SHADOW / CONFIRMATION / AUTO des stratégies hybrides.
>
> ⚠️ **Statut des sources.** Les pages officielles du centre d'aide d'Apex (« Prohibited
> Activities », « Legacy PA Compliance ») bloquent la lecture automatisée (HTTP 403). Cette
> synthèse est reconstruite à partir de sources secondaires fiables (TradersPost, PickMyTrade,
> TradeDupe, QuantVPS) et de la lecture qu'un moteur de recherche a faite des pages
> officielles. **Apex dit lui-même que la politique évolue** — les chiffres et cas limites
> sont à reconfirmer directement au support avant tout déploiement réel. Comme dans
> [sonde-manuelle-apex.md](sonde-manuelle-apex.md), **c'est toi la source d'autorité sur ton
> propre compte** : ce document éclaire la règle, il ne la remplace pas.

## Le principe derrière la règle

Apex ne bannit pas « l'automatisation » en bloc : il bannit **le système qui trade à ta place
sans toi**. Leur justification textuelle — les *rewards* récompensent « un trader humain qui
participe activement », pas « un système qui exécute une logique préprogrammée ». Toute la
ligne de démarcation tient dans un concept :

> **La supervision active — un humain peut intervenir à tout moment.**

Ce n'est pas la présence ou l'absence de code qui compte, c'est de savoir si un humain initie
et peut arrêter chaque geste.

## Ce qui est formellement interdit

- **Les systèmes pleinement automatisés** : EA, bots, IA, algorithmes, HFT qui *entrent et
  sortent seuls* des positions.
- **Le « set-and-forget »** : un bot qui roule 24/7 sans surveillance. C'est le cas de figure
  explicitement visé.
- **Le HFT / haute fréquence** : une source cite un seuil de rétention sous **~2 secondes**
  comme signal d'alarme.
- **L'arbitrage de latence** : exploiter l'écart entre le prix simulé de ton compte et le vrai
  marché. Point sensible ici, parce que les comptes Apex sont en environnement simulé —
  catégorie distincte des « bots », visée spécifiquement.
- **Les EA/bots loués ou commerciaux** et **les services de signaux loués partagés entre
  plusieurs traders** : bannis parce qu'ils créent du risque corrélé entre comptes de
  personnes différentes.
- **Le hedging interne** entre tes propres comptes (compte A long NQ pendant que compte B est
  short NQ = échec de conformité immédiat).

## Ce qui est permis (la zone grise à bien comprendre)

- **La gestion semi-automatique d'une position déjà ouverte** : les *ATM strategies* (bracket,
  stop suiveur, mise à breakeven). Tu entres à la main, l'outil gère la sortie.
- **Les alertes / webhooks** (TradingView → webhook → broker) **à condition que tu supervises**.
  Formulation citée : « TradingView alert + webhook + broker = compliant if you supervise. »
- **Tes propres scripts** : tes NinjaScript / EA maison que *tu* surveilles sont acceptés.
  C'est la propriété + la supervision qui les distingue des bots loués.
- **Les bots DCA** (moyenne d'achat) — cités comme permis par une source ; à traiter avec
  prudence, absent des pages officielles.
- **Le copy trading entre TES propres comptes** : permis sur les évaluations *et* les comptes
  financés, **si** tout part d'un seul compte maître/leader et que tous tes comptes sont **du
  même côté du marché en même temps**. (C'est ce qui réconcilie la contradiction entre
  sources : copier entre *tes* comptes = OK ; copier depuis/vers le compte d'un *autre*
  trader = interdit.)

## Ce que ça implique pour NOTRE projet

La frontière opérationnelle — *« actively monitored, a human can intervene »* — cartographie
directement nos trois modes ([strategies-hybrides.md](strategies-hybrides.md)) :

| Mode | Description | Statut Apex |
|------|-------------|-------------|
| **SHADOW** | décisions journalisées, zéro ordre | ✔️ Hors de portée du règlement — ne trade pas |
| **CONFIRMATION** | pop-up accepte/refuse, l'humain initie chaque position | ✔️ Conforme — c'est le semi-automatisé supervisé visé par la règle |
| **AUTO** | entrées/sorties pleinement autonomes | ❌ Du mauvais côté de la ligne sur un compte financé |

Le pop-up de confirmation n'est **pas cosmétique** : c'est *l'élément* qui rend le montage
défendable. Un déclencheur automatique suivi d'un clic humain obligatoire laisse une trace
comportementale humaine — c'est aussi ça qui protège le compte, au-delà de la conformité sur
papier. Le réglage H2 (l'humain initie chaque **position**, la gestion protectrice — stop
suiveur, flat de séance — est automatique) repose sur cette lecture ; voir le point ci-dessous.

## Deux nuances de version

- **Évaluation vs compte financé (PA)** : les sources divergent. Certaines disent l'automatisation
  tolérée pendant l'évaluation mais bannie sur le PA financé ; d'autres disent que les deux
  exigent le même semi-auto supervisé. **Lecture prudente : traiter le compte financé comme la
  zone stricte**, n'assumer aucune tolérance côté éval.
- **3.0/Legacy vs 4.0** : la règle « one-directional » (pas de long et short simultanés sur le
  même instrument) a été **retirée en 4.0**, mais les comptes **Legacy 3.0 la conservent**. La
  page officielle « Legacy PA Compliance » ne vise que ces vieux comptes.

## La question à faire trancher (avant tout déploiement AUTO ou CONFIRMATION sur Apex réel)

Le seul point d'interprétation qui gouverne l'architecture, à confirmer **directement au
support Apex** :

> Un déclencheur automatique suivi d'une **confirmation humaine obligatoire** avant chaque
> position compte-t-il comme « supervisé » au sens de leur règle courante ? Et la gestion
> protectrice automatique (stop suiveur, flat de séance) sur une position que l'humain a
> initiée reste-t-elle admise ?

Tant que ce n'est pas confirmé, s'en tenir à SHADOW (gratuit, zéro risque) et à la sonde
manuelle de [sonde-manuelle-apex.md](sonde-manuelle-apex.md).

## Le pattern qui déclenche une enquête

La limite n'est pas qu'écrite, elle est **interprétative et rétroactive** : Apex se réserve le
jugement, applique surtout au moment de la demande de *payout*, et la sanction va jusqu'à la
fermeture du compte et le refus/clawback du retrait. Les signaux qui attirent l'attention :
cadence robotique inhumaine, rétention ultra-courte répétée, activité 24/7 sans pause, entrées
à la milliseconde.

## Sources (consultées le 2026-07-22)

- Apex — Prohibited Activities (support officiel, non lisible en direct) :
  https://support.apextraderfunding.com/hc/en-us/articles/40463668243099-Prohibited-Activities
- Apex — Legacy Performance Account Compliance :
  https://support.apextraderfunding.com/hc/en-us/articles/31519788944411-Legacy-Performance-Account-PA-Compliance
- TradersPost — Complete Apex Trader Funding Guide :
  https://blog.traderspost.io/article/apex-trader-funding-review
- PickMyTrade — Apex Trader Funding FAQ (2026) :
  https://pickmytrade.trade/prop-firm-faq/apex-trader-funding-faq/
- TradeDupe — Apex Copy Trading Rules (2026) :
  https://tradedupe.com/apex-copy-trading-rules
- QuantVPS — Does Apex Allow Automated Trading Bots? :
  https://www.quantvps.com/blog/apex-trader-funding-automated-trading-bots
