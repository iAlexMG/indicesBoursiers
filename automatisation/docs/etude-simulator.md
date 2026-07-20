# Étude — compte papier (Trading Simulator) vs exécution régulière (volet B)

> Rédigée le 2026-07-19. Méthode Phase 0 : **vérifier/mesurer, jamais supposer**. Chaque fait
> est marqué **MESURÉ** (réflexion sur `TradingPlatform.BusinessLayer.dll` v1.146.14 — dump de
> 846 types publics via la console `poc/Phase0Poc` — ou constat sur le poste), **DOC**
> (documentation officielle Quantower, liens en bas), ou **À SONDER** (exige un run live).
> Aucun ordre n'a été passé, rien n'a été déployé dans Quantower.

## Verdict en cinq points

1. **Même code : OUI (MESURÉ).** L'API d'ordres ne distingue jamais sim et réel : le compte
   est un **paramètre de la requête** (`OrderRequestParameters.Account` / `ConnectionId`).
   Une seule stratégie, un paramètre « Compte », et elle pointe le papier ou l'Apex.
2. **Accessibilité : le Trading Simulator est PAYANT (DOC)** — absent de la version
   gratuite ; inclus dans Multi-Asset, All-in-One (70 $ US/mois, dégressif) et le pack
   « Advanced Features ». **Mais l'API de stratégies (Strategy Runner) est GRATUITE**, et un
   **essai complet de 7 jours** existe. Le POC tient dans une semaine d'essai.
3. **Tous les mécanismes des specs existent dans l'API (MESURÉ)** : brackets `SlTpHolder`
   (avec `IsTrailing`), `ModifyOrder`, `CancelOrder`/`ClosePosition`, OCO
   (`PlaceOrders(…, GroupOrderType.OCO)`), flat par compte (`CancelOrders(account)` +
   `ClosePositions(account)`), événements complets. Ce qui reste ouvert : la **disponibilité
   par connexion** (`Vendor.GetAllowedOrderTypes`) — Rithmic vs Simulator — → sonde.
4. **Le Simulator émule l'exécution PAR-DESSUS les connexions branchées (DOC)** : avec
   Rithmic connecté, le papier s'exécute sur le flux réel Apex — même session, pas de
   conflit. ⚠️ Chaque démarrage/arrêt du panneau **crée un compte neuf** (pas de persistance).
5. **Où vit le stop (serveur Rithmic vs plateforme) : NON TRANCHÉ.** Indice MESURÉ :
   `Core.LocalOrders` (un moteur d'« ordres locaux » côté plateforme existe). Sans gravité
   pour le POC papier ; **question de sécurité majeure pour la phase 5** → sonde + question
   au support Quantower, à poser dès maintenant (gratuite).

---

## 1. Accessibilité — MESURÉ sur le poste + DOC

**Sur le poste (MESURÉ)** :
- `bin\Vendors\TradingSimulatorVendor\` et `bin\plug-ins\TradingSimulatorPanel\` sont
  **installés** (v1.146.14) — le moteur et son panneau font partie de la plateforme, pas d'un
  téléchargement à part. 49 vendors présents, dont `RithmicVendor`, `EmulatorVendor`,
  `MarketReplayVendor`, `BacktestResultVendor`.
- `ConnectionType` (enum) = `General, TradingSimulator, HistoryPlayer, Backtester, Technical`
  → le papier est un **type de connexion de plein droit** dans le BusinessLayer.
- `settings.xml` contient le réglage
  `ConfirmTradingOnRealConnectionWithRunningSimulator` (+ l'événement
  `Core.OnAskUserConfirmationForTradingWithRunningEmulator`) : la **cohabitation sim + réel
  est prévue d'origine**, avec garde-fou de confirmation côté plateforme.
- La connexion Simulator n'apparaît pas dans `settings.xml` : elle est créée par le vendor au
  démarrage.

**Licences (DOC — [comparaison officielle](https://help.quantower.com/quantower/getting-started/account-and-licensing/license-comparison.md))** :

| Fonction | Free | Crypto | Multi-Asset | All-in-One |
|---|---|---|---|---|
| **Trading Simulator** | ❌ | crypto seulement | ✔️ | ✔️ |
| **Strategy Runner / API algo** | ✔️ **GRATUIT** | ✔️ | ✔️ | ✔️ |
| Market Replay | ❌ | crypto seulement | ✔️ | ✔️ |

- Prix ([pricing](https://www.quantower.com/pricing)) : All-in-One **70 $ US/mois**, dégressif
  (-10 % à 3 mois, -20 % à 6, -30 % à l'année) ; features à la carte ; **essai 7 jours
  complet** sur inscription ; garantie 10 jours. Le
  [pack « Advanced Features »](https://www.quantower.com/advancedfeatures) contient le
  Trading Simulator (prix à la carte à lire dans le configurateur de la page pricing).
- ⚠️ **La thèse du site ne se transpose PAS ici.** « La plateforme verrouille SES panneaux,
  pas la donnée dessous » vaut pour la *lecture de données ouvertes* ; le Simulator est un
  **moteur licencié**, pas une donnée. Contourner son verrou par l'API serait d'une autre
  nature — **on ne le fera pas**. Les voies : essai 7 jours, achat, ou alternatives (§7).
- Vérification 5 min restante (utilisateur, gratuite) : ouvrir le panneau Trading Simulator
  sur le poste sans licence et noter le comportement (pop-up licence type DOM-Surface
  attendu — le confirmer documente le point d'entrée du verrou).

## 2. Un seul code — MESURÉ

- `OrderRequestParameters` porte : `Account`, `AccountId`, `ConnectionId`, `Symbol`, `Side`,
  `Quantity`, `OrderTypeId`, `Price`, `TriggerPrice`, `TrailOffset`, `StopLoss`/`TakeProfit`
  (`SlTpHolder`), `TimeInForce`, `Slippage`, `AdditionalParameters`.
- `Core.Instance.PlaceOrder(PlaceOrderRequestParameters)` → `TradingOperationResult`
  (`Success`/`Failure`) — **aucune API parallèle pour le papier**. (`PaperRequest` existe
  mais hérite de `PlaceOrderRequestParameters` — même moule.)
- L'identité d'un compte = `AccountComplexIdentifier { AccountId, ConnectionId }` : viser le
  compte du Simulator ou le compte Apex est **le même geste**.
- `[InputParameter]` accepte `Symbol` (mesuré Phase 0) ; `Account` passe par la même
  sérialisation `SettingItem` → **quasi certain**, la compile de la sonde le confirmera.

**Réponse à la question d'ouverture du chantier : coder pour le Simulator et coder pour le
réel, c'est LE MÊME code.** Le choix se fait au démarrage de la stratégie, par paramètre.

## 3. Cycle de vie des ordres — l'API a tout (MESURÉ) ; la dispo par connexion À SONDER

Correspondance mécanisme des specs → API mesurée :

| # | Mécanisme (spec) | API mesurée | Verdict |
|---|---|---|---|
| 1 | Bracket SL/TP attaché (H1, H3) | `StopLoss`/`TakeProfit` = `SlTpHolder.CreateSL(prix, Absolute\|Offset, trailing, …)` / `CreateTP(…)` posés **avec** l'ordre d'entrée ; `Position.StopLoss`/`TakeProfit` = les ordres liés, vivants | **API OUI** |
| 2 | Modification du stop (H2 suiveur) | `Core.ModifyOrder(order, tif, price, triggerPrice, qty, trailOffset)` ou `ModifyOrderRequestParameters(OrderId)` ; et l'opération dédiée `AdvancedTradingOperations.AdjustStopLoss(position, orders, SlTpHolder)` | **API OUI** |
| 3 | Annulation du bracket (H2 inverse, H3 RSI 50) | `order.Cancel()` / `Core.CancelOrder` ; `ClosePosition` (le sort des brackets liés à la fermeture : attendu = annulés, **à confirmer à la sonde**) | **API OUI** |
| 4 | Tout annuler + liquider (flat 16:55, garde-fou, kill) | `AdvancedTradingOperations.CancelOrders(account)` + `ClosePositions(account)` — **par compte, en deux appels** | **API OUI** |
| 5 | Événements de fill | `Core.TradeAdded`, `OrderAdded`/`OrderRemoved`, `Order.Updated`, `OrderStatus` (`PartiallyFilled`, `Filled`, `Cancelled`, `Refused`…), `AverageFillPrice`/`FilledQuantity`, `Position.Updated`, `ClosedPositionAdded` | **API OUI** |
| 6 | Où vit le stop | `Core.LocalOrders` = moteur d'ordres **côté plateforme** (indice) | **NON TRANCHÉ** → §6 |
| 7 | OCO d'entrée (variante H1) | `Core.PlaceOrders(collection, GroupOrderType.OCO)` | **API OUI** |
| 8 | Compte en paramètre | §2 | **OUI** |
| 9 | Seed `GetHistory` + barres 3/5 m | mesuré Phases 0 et 3 (extracteur, indicateurs) | **OUI** |

⚠️ **La réserve transversale** : les types d'ordres effectivement servis viennent du vendor
(`Vendor.GetAllowedOrderTypes()`, par connexion). `TrailingStopOrderType` et
`OrderTypeBehavior.TrailingStop` existent dans la plateforme, mais **Rithmic et le Simulator
n'exposent pas forcément la même liste**. La sonde (§9) commence par dumper cette liste des
deux côtés. Repli déjà prévu par la spec H2 : si le trailing natif manque quelque part, le
suiveur = mécanisme 2 (modification périodique) — qui, lui, est universel.

## 4. Le flux du Simulator — DOC + À SONDER

- **DOC** : le Simulator « émule l'exécution d'ordres pour n'importe quelle connexion, y
  compris celles qui ne permettent pas de trader » — il exécute sur les **données temps réel
  des connexions branchées**. Avec Rithmic connecté : le papier s'exécute sur le flux
  Apex/Rithmic. **Même session Rithmic, pas de session en plus** (la contrainte « 1 session
  Apex » ne s'applique pas ici).
- **DOC** : réglages du panneau = balance initiale, **délai d'exécution**, commission,
  netting. Un compte par devise de base.
- **DOC ⚠️** : **chaque démarrage/arrêt du panneau crée un compte NEUF.** Conséquence pour le
  critère de succès « redémarrage propre le lendemain » : la continuité vit dans le **journal
  NDJSON**, pas dans le compte (et la stratégie retrouve son compte par NOM de connexion/type,
  pas par Id figé).
- Marché fermé : pas de données → pas de fills. Cohérent avec le cadre des specs (flat 16:55,
  aucune entrée après 15:30) et l'horaire CME déjà encodé côté affichage.
- **À SONDER** : granularité du moteur de fills (sur trades ? sur quotes bid/ask ?), effet du
  délai configuré, comportement d'une limite posée dans le spread.

## 5. Fidélité — DOC (mince) + SONDE

- Configurable : délai, commission, netting, balance. Non documenté : slippage des markets,
  fills partiels, file d'attente aux limites. La sonde mesurera : market → fill au bid/ask ?
  stop déclenché sur trade ou sur quote ?
- À 1 contrat, les fills partiels sont non-sujet pour le POC (noté quand même).
- Rappel du cadrage : la fidélité **borne ce que la phase 4 peut conclure**, elle n'est pas
  l'objectif. Le POC prouve la mécanique.

## 6. Où vit le stop — NON TRANCHÉ, et c'est la question de la phase 5

- **MESURÉ** : `Core.LocalOrders` (`LocalOrdersManager.TryHandleTradingOperationRequest`) —
  la plateforme possède un moteur capable d'**intercepter et tenir des ordres localement**.
  Cohérent avec un modèle où les `SlTpHolder` sont émulés par la plateforme pour les
  connexions sans support natif. Ça ne dit pas ce que fait la connexion **Rithmic**.
- Conséquence si les stops vivent côté plateforme : **Quantower fermé/planté = stop mort.**
  Pour le POC papier : sans gravité. Pour la phase 5 (compte Apex) : question de sécurité
  **majeure**, à trancher AVANT tout ordre réel.
- Deux gestes, dès maintenant : (1) la sonde compare le comportement (stop visible côté
  serveur ? survit-il à un redémarrage de la plateforme ?) ; (2) **question au support
  Quantower** (gratuite) : « sur une connexion Rithmic, les SL/TP attachés sont-ils tenus
  serveur ou plateforme ? ».

## 7. Les voies d'accès, pesées (la licence n'est pas prise aujourd'hui)

| Voie | Coût | Ce qu'elle prouve | Verdict |
|---|---|---|---|
| **Essai 7 jours complet** (inscription Quantower) | 0 $ | tout le critère de succès — une semaine de séances NY suffit au POC, qui est taillé petit exprès | **recommandée en premier** |
| Shadow mode pur (Strategy API gratuite : signaux + journal, zéro ordre) | 0 $ | les **décisions**, pas la mécanique d'ordres | complément gratuit, pas un substitut ; c'est déjà la phase 4 du plan |
| Achat (pack Advanced Features / Multi-Asset / All-in-One 70 $/mois dégressif, garantie 10 j) | $ | tout, sans limite de temps | décision de coût = utilisateur |
| Compte Apex lui-même | risque réel (règles d'éval, reset) | la mécanique **serveur** réelle | ⛔ c'est la phase 5 avec garde-fous, PAS un bac à sable |
| Contourner le verrou par l'API | — | — | ⛔ **exclu** : un moteur licencié n'est pas une donnée ouverte |

## 8. Architecture recommandée (phases 4-5)

- **La logique de stratégie et la mécanique d'ordres : en C# (`Strategy`)** — l'API d'ordres
  et ses événements vivent là, gratuitement, au plus près des fills. Compte en
  `[InputParameter]`.
- **Mince et paramétrée** : tout ce qui peut être un paramètre plutôt que du code — pour
  amortir le piège 5 (toute modif C# = redémarrer Quantower). **Les 3 stratégies = 3 classes
  dans UNE DLL** (patron NqFeed + Probe) → un seul deploy, un seul redémarrage.
- **Le journal NDJSON écrit par la stratégie** : file bornée + thread d'écriture (patron du
  pont), `InvariantCulture` (piège 6).
- **Python = l'analyse, jamais le chemin d'ordres** : lecture des journaux, parité avec les
  jumeaux LEAN (volet C). Un pont d'ordres TCP vers Python reste possible plus tard, mais il
  ajoute une surface de panne sur le chemin le plus critique — pas pour le POC.
- ⚠️ **Découverte de build (MESURÉE, 2026-07-19)** : la DLL v1.146.14 référence
  `System.Runtime 10.0.0.0` → **toute nouvelle compile contre elle doit cibler `net10.0`**
  (sinon CS1705). Les DLL `net8.0` déjà déployées continuent de charger. `Phase0Poc` a été
  compilé en passant `-p:TargetFramework=net10.0` en ligne de commande (csproj inchangé) ;
  prévoir la même chose (ou une mise à jour des `.csproj`) pour toute nouvelle stratégie.

## 9. La sonde proposée — prochain pas concret (à faire VALIDER avant tout déploiement)

**« Ordres Probe (SIM) »** — une `Strategy` minimale, à lancer pendant l'essai 7 jours, en
séance, sur le compte du Simulator :

1. dumpe `GetAllowedOrderTypes` de la connexion du compte (règle la réserve du §3) — et le
   même dump sur la connexion Rithmic, **sans y passer d'ordre** ;
2. place UN market ×1 avec bracket (`SlTpHolder` SL offset 20 ticks / TP 40) ;
3. modifie le SL deux fois (suiveur simulé) ;
4. sort par `ClosePosition` et note le sort du bracket ;
5. rejoue 2-4 en terminant par TP touché puis par SL touché ;
6. journalise tout en NDJSON, puis `CancelOrders(account)` + `ClosePositions(account)` et stop.

**Garde-fou codé en dur** : la sonde **refuse de démarrer** si
`connexion.Type != ConnectionType.TradingSimulator` — impossible de la pointer par accident
sur l'Apex.

## Décisions — tranchées le 2026-07-19 (même soir)

1. **Voie d'accès : ESSAI 7 JOURS retenu** (décision utilisateur). ⚠️ **Ne PAS l'activer tout
   de suite** : le compteur part au premier jour. Séquence : coder d'abord le volet C, la
   sonde et les 3 stratégies live ; activer l'essai SEULEMENT quand tout est prêt à rouler,
   pour que la semaine serve au complet (5 séances NY).
2. Vérif 5 min du panneau sans licence : optionnelle, non bloquante (l'essai la rendra sans
   objet).
3. **Question des stops au support Quantower : STAND-BY** (décision utilisateur — gardée en
   mémoire). À poser au plus tard **avant la phase 5** : « sur une connexion Rithmic, les
   SL/TP attachés sont-ils tenus côté serveur ou côté plateforme ? ». La sonde apportera un
   premier élément (survie du stop à un redémarrage).

---

**Sources** : réflexion `TradingPlatform.BusinessLayer.dll` v1.146.14 (dump du 2026-07-19,
outil `poc/Phase0Poc`) ; poste (`bin\Vendors\`, `settings.xml`) ;
[Trading simulator](https://help.quantower.com/quantower/trading-panels/trading-simulator) ·
[License comparison](https://help.quantower.com/quantower/getting-started/account-and-licensing/license-comparison.md) ·
[Pricing](https://www.quantower.com/pricing) ·
[Advanced Features](https://www.quantower.com/advancedfeatures).
