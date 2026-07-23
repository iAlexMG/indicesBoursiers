# Protocole de captures — automatisation indices (Apex / Quantower)

Objectif : produire les **images et la vidéo** qui manquent au pilier « Automatisation »
du site (volet indices), pour démontrer la **chaîne d'exécution live** sur Apex — pas la
rentabilité (cadrage fondamental du projet). Ce doc est le mode d'emploi de la prise ;
il se rouvre à chaque session de capture.

Quantower est une app de bureau : c'est **toi** qui arranges l'écran et qui captures.
Moi (Claude) je fais le **post** : recadrage, floutage résiduel, annotation du fil 1→6,
triptyque, montage de la boucle vidéo, puis intégration au site (`site-content/` →
`sync-site.py` → push).

## Décisions figées (2026-07-23)

- **Un seul écran 1080p** (1920×1080). Le cockpit tient dans une fenêtre dense.
- **Terminal NDJSON visible** dans le cadre, via `suivre-journal.ps1` (lecture lisible).
- **Vidéo = boucle muette ~30–60 s**, autoplay/muted/loop ; le fil 1→6 en surimpression
  (ajouté au montage) sert de narration.
- **Fill + bracket réels montrés via le panneau Ordres/DOM de Quantower**, PAS via le
  journal : sur le compte réel, les événements de position ne sont pas journalisés
  (constat du 07-22, voir Pièges). Le terminal ne porte donc que la chaîne de *décision*.
- Le moment filmé = **mode CONFIRMATION** (pop-up → clic → vrai ordre + bracket).

## 1. Le cockpit — disposition 1080p

Quatre zones + le pop-up flottant. Le fil numéroté 1→6 est l'ordre de lecture (annoté en post).

- **Graphique NQ 1 m** — gauche, ~64 % de large, pleine hauteur sauf le bandeau du bas.
  Charge la stratégie **et** son indicateur visuel jumeau (`Hybride H2 SMA Suiveur (visuel)`,
  etc.) : SMA 9 (bleue) / 21 (ambre), escalier ambre du stop suiveur, bande rouge→verte
  risque/profit, triangle d'entrée, point de sortie, étiquette `+pts (R)`, panneau résultats
  haut-droite — **plus** les lignes d'ordre réelles (fill + SL + TP) tracées par Quantower.
- **Colonne droite ~36 %**, coupée en deux :
  - haut : **Positions / Ordres** (+ DOM au besoin) → doit lire *LONG 1 rempli · SL travail · TP travail*.
  - bas : panneau **Stratégie** déplié sur « Mode d'exécution » → prouve `CONFIRMATION`
    (et que `AUTO` est refusé hors Simulator).
- **Bandeau bas, pleine largeur, ~20 % de haut** : le **terminal** (`suivre-journal.ps1`).
- **Pop-up de confirmation** : flotte au-dessus du chart quand il se déclenche (un temps, pas une zone).

Le fil : **1** signal (croisement) → **2** proposition (pop-up) → **3** journal/terminal →
**4** fill + bracket (Ordres) → **5** stop traîné (escalier) → **6** sortie.

## 2. Réglages de lisibilité (fait ou casse l'image)

- **Thème sombre** Quantower ; **polices agrandies** (axes, grille des ordres, et surtout la
  taille de l'indicateur visuel). Le texte doit survivre à la réduction à ~1200 px de large.
- **Capture en 1920×1080 natif** — jamais une fenêtre réduite puis agrandie.
- **Déclutter** : ferme watchlists, chat, panneaux hors-sujet ; réduis les barres d'outils.
- **Masquage du compte, DEUX endroits** :
  1. l'en-tête Quantower (garder « Apex Legacy 250k » lisible, cacher le numéro) ;
  2. le NDJSON lui-même — la ligne `demarrage` contient le numéro **et le nom légal**.
     `suivre-journal.ps1` le masque tout seul (`APEX-**** (compte masqué)`). Si tu tail le
     JSON brut à la place, c'est à toi de le gérer.
- **Palette = celle du code** (rien à réinventer) : SMA 9 bleue, SMA 21 + stop ambre, entrée
  verte, sortie rouge, TP bleu. Réutilisée dans l'annotation → l'œil relie image et légende.

## 3. Le terminal — `suivre-journal.ps1`

Dans `automatisation/captures/`. Lit le `.ndjson` en direct et l'affiche proprement
(couleurs, heure ET, compte masqué, UTF-8 géré). Le fichier reste un vrai `.ndjson` lu tel
quel → c'est le **même format que le jumeau LEAN**, donc la preuve de parité tient.

```powershell
# lance d'abord la stratégie (ça crée le fichier du jour), puis :
.\suivre-journal.ps1 H1              # H1=sma_bracket_nq, H2=sma_suiveur_nq, H3=sma_annule_nq
.\suivre-journal.ps1 H2 -Tail 20
.\suivre-journal.ps1 -Fichier 'H:\...\sma_bracket_nq\2026-07-22.ndjson' -Instantane   # rejoue une séance passée
```

Rendu (extrait réel, séance CONFIRMATION du 07-22) :

```
  18:07:08  DÉMARRAGE     CONFIRMATION @ APEX-**** (compte masqué) (General) — 24 h ...
  18:21:00  SIGNAL       @29090.25 croisement haussier -> long   [ sma 29085.42/29082.46  atr 12.57 ]
  18:21:00  PROPOSITION  @29090.25 Hybride H1 SMA Bracket (NQ) : ENTRÉE LONG ? — market ×1 ... + SL 75 / TP 75 ticks
  18:21:02  OK ACCEPTÉE   ACCEPTÉE par l'utilisateur (clic)
  18:21:02  ENTRÉE       @29090.25 market ×1 + SL 75 ticks / TP 75 ticks
```

Repli (tail brut, si besoin — **`-Encoding UTF8` obligatoire**, sinon accents cassés) :

```powershell
$dir = 'H:\IndicesBoursiers\automatisation\journaux\sma_bracket_nq'
$f = Get-ChildItem $dir -Filter *.ndjson | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $f.FullName -Wait -Tail 20 -Encoding UTF8
```

Chemins réels : `H:\IndicesBoursiers\automatisation\journaux\<slug>\<AAAA-MM-JJ ET>.ndjson`.
Un sous-dossier par stratégie, **un fichier par jour ET**. (`orb_nq\` est un vestige de
l'ancien nommage ORB — ignorer.)

## 4. Scénario de la vidéo (30–60 s, boucle muette)

Enregistre en **1080p avec OBS Studio** (sortie mp4 propre). Un cycle CONFIRMATION suffit :

| t | À l'écran | Beat |
|---|---|---|
| 0–5 s | vue d'ensemble : SMA qui convergent, terminal calme | plan large |
| ~5 s | **croisement SMA 2/6** → signal détecté | **1** |
| ~8 s | **pop-up de confirmation** (« ENTRÉE LONG ? ») | **2** |
| ~12 s | `SIGNAL` puis `PROPOSITION` défilent au terminal | **3** |
| ~14 s | **clic Confirmer** → **ordre rempli + bracket SL/TP** dans Ordres et sur le chart | **4** |
| 15–45 s | le **stop suiveur monte marche par marche** (escalier ambre) ; `stop_modifie` défile (H2) | **5** |
| fin | **sortie** (stop touché ou croisement inverse) + étiquette `+pts (R)` | **6** |

Note : beats **4** (fill/bracket) se lisent dans **Ordres/DOM** (pas le terminal). Le stop
suiveur **5** n'existe que pour **H2** — pour filmer l'escalier, tourne la séance en H2.

## 5. Les 4 crops de détail

Depuis la même session (ou 2–3 essais) :

- **A** — le **pop-up de confirmation** seul.
- **B** — l'**ordre réel + bracket** dans Ordres/DOM (la vérité plateforme du fill).
- **C** — les **3 visuels** OnPaintChart, une capture chacun (H1 bracket / H2 escalier / H3
  losange d'annulation) → je fais le triptyque.
- **D** — le menu déroulant **« Mode d'exécution »** ouvert (SHADOW / CONFIRMATION / AUTO refusé).

## 6. Livraison et post-traitement

Tu déposes dans un dossier (à convenir — scratchpad ou `H:\...\captures\`) : la **vidéo brute**
+ les **PNG bruts**. Je fais : recadrage/nettoyage, floutage résiduel, annotation 1→6,
triptyque, montage de la boucle (coupe, mute, surimpression, mp4 web léger), puis intégration
dans `Portfolio/indicesBoursiers/automatisation/site-content/` → `sync-site.py indices` → push.

## Pièges — à relire avant chaque prise

- **Fuite du compte dans le NDJSON** : la ligne `demarrage` contient numéro + nom légal.
  `suivre-journal.ps1` masque ; le tail brut ne masque pas.
- **Encodage** : PowerShell 5.1 lit un `.ndjson` UTF-8 en CP1252 → accents cassés à l'écran.
  Toujours `-Encoding UTF8` (le suiveur le force).
- **Fill/bracket réels non journalisés** (constat 07-22 : la trace réelle s'arrête à
  `entree_envoyee`). Ne PAS compter sur le terminal pour montrer le fill sur le compte réel
  — le montrer via le panneau Ordres. (En SHADOW, à l'inverse, le journal est complet.)
- **`.ps1` avec BOM UTF-8** (piège PS 5.1 : un `—` dans une chaîne casse le parse sinon).
- **Stop suiveur = H2 seulement** ; le pop-up d'entrée = les trois ; l'annulation au
  croisement inverse = H3.

## Renvois

Mémoire : `ialexmg-automatisation-hybrides` (les 3 hybrides, 3 modes, chaîne prouvée),
`apex-regles-automatisation`, `ialexmg-site-public-cible`, `powershell-ps1-bom-cp1252`,
`terminologie-venue-exchange`, `style-quebecois-anti-ia`.
Code : `hybrides/HybrideStrategyBase.cs` (modes, journal, propositions),
`hybrides/JournalNdjson.cs` (format + chemin), `indicators/Sma*Visuel/` (les 3 visuels).
Prompt de reprise du pilier : `Prompt_Automatisation_Hybrides.md`.
