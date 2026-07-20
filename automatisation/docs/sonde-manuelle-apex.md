# Sonde MANUELLE sur le compte Apex — la mécanique d'ordres sans bot

> Écrite le 2026-07-20, après la mort de l'essai 7 jours du Trading Simulator. Contrainte
> posée par l'utilisateur (source d'autorité sur son compte) : **Apex interdit les
> transactions automatisées et les bots** — donc ni les stratégies hybrides ni la sonde
> logicielle « Ordres Probe (SIM) » ne toucheront ce compte. Le **trading manuel**, lui,
> est l'usage normal du compte. Cette page répond à la checklist mécanique de
> [etude-simulator.md](etude-simulator.md) §3 **à la main**, dans les panneaux Quantower.

## Ce que ça prouve / ne prouve pas

- ✔️ Prouve : la PLATEFORME sait faire (bracket attaché, modification du stop, annulation
  au close, TP/SL exécutés) sur la connexion Rithmic/Apex réelle.
- ❌ Ne prouve PAS : que NOTRE code place ces ordres via l'API — ça, c'est le Trading
  Simulator (payant) ou la phase 5. Le mode SHADOW couvre, lui, les décisions.

## Préparation

- Contrat : **MNQ ×1** (2 $/pt — dix fois moins cher que NQ) ; séance calme, hors annonces.
- ⚠️ Les pertes de l'exercice sont PETITES (quelques ticks) mais comptent dans le
  drawdown de l'éval — c'est toi qui juges le bon moment. Aucun chiffre de règle Apex
  n'est supposé ici : tu es la source.
- Panneaux ouverts : graphique, DOM (ou panneau d'ordre), **Orders**, **Positions**.

## Le déroulé (noter chaque réponse — 10 minutes)

1. **Bracket posé** : market ×1 MNQ avec SL/TP attachés (SL ≈ 20 ticks, TP ≈ 40) depuis le
   panneau d'ordre. → Les DEUX ordres liés apparaissent-ils dans Orders ? Tout de suite ou
   après un délai ?
2. **Modification** : glisser le SL 2 fois (quelques ticks). → Acceptée les deux fois ?
   Le prix affiché suit-il ?
3. **Annulation au close** : fermer la position (bouton X / Close). → Le bracket est-il
   annulé TOUT SEUL, ou reste-t-il des ordres orphelins dans Orders ?
4. **TP touché** : re-market ×1 avec TP à 2-3 ticks (SL loin). → Fill du TP, position
   fermée proprement ?
5. **SL touché** : re-market ×1 avec SL à 2-3 ticks (TP loin). → Fill du SL ?
6. **Flat en un geste** : re-market ×1 avec bracket, puis le bouton **Flatten** du compte.
   → Tout annulé + liquidé d'un coup ?

## ⛔ À NE PAS faire

**Ne PAS fermer Quantower avec une position ouverte** pour tester la survie du stop : si
le stop vit côté plateforme, la position resterait NUE sur ton compte. La question « SL/TP
attachés sur Rithmic : tenus serveur ou plateforme ? » se pose au **support Quantower**
(gratuit — question en stand-by, recommandée MAINTENANT que le Simulator est hors jeu).
