# POC Phase 0 — mesures de faisabilité

Deux composants :

- **`Phase0Strategy/`** ⭐ (voie retenue) — une `Strategy` Quantower qui tourne **dans** la
  plateforme (connexion Rithmic déjà authentifiée) et mesure Q2–Q4 + specs du contrat. Voir
  « Stratégie de mesure » plus bas.
- **`Phase0Poc/`** — console C# `net8.0` référençant le BusinessLayer **hors** Quantower.
  A servi à prouver que la connexion standalone est bloquée (mot de passe Rithmic non
  déchiffrable hors process) et reste un **outil de diagnostic d'API** (modes `dump`/`type`/
  `connect`).

## ⭐ Stratégie de mesure `Phase0 Measure (NQ)`

Compiler + déployer dans le dossier Scripts de Quantower :

```powershell
# résout le bin v* le plus récent, build, et copie la DLL dans Settings\Scripts\Strategies
powershell -File poc\Phase0Strategy\deploy.ps1     # ou reproduire les 2 étapes à la main
```

Puis dans Quantower (connecté à Rithmic) : panneau **Strategies** → `Phase0 Measure (NQ)` →
choisir le symbole **NQ** → **Run**. Sortie : `docs/phase0-measures.txt` (+ logs de la stratégie).

## Console de diagnostic (Phase0Poc)

## Compiler & lancer

Le chemin Quantower est versionné (`v*`) : jamais en dur. `build.ps1` résout le dossier le
plus récent et le passe au build. Équivalent en une ligne (sans `-ExecutionPolicy Bypass`) :

```powershell
$latest = Get-ChildItem "C:\Quantower\TradingPlatform" -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending | Select-Object -First 1
dotnet build poc\Phase0Poc\Phase0Poc.csproj -c Release -p:QuantowerBin="$($latest.FullName)\bin"

# Lancer (depuis le dossier de sortie ou par chemin) :
dotnet poc\Phase0Poc\bin\Release\net8.0\Phase0Poc.dll <mode>
```

Modes : `dump` (inventaire réflexif de l'API → fichier), `type <FQN>` (membres virtuels/
protégés d'un type), `connect` (Initialize + liste des connexions/vendors), `rithmic`
(prouve le verrou : `Fail "Password is empty."` hors process).

## Note : la connexion standalone est un cul-de-sac (mesuré)

Le mot de passe stocké par Quantower dans `settings.xml` (chiffré) **ne se déchiffre pas** hors
du process Quantower (`FailedToRestorePassword = True`). D'où le pivot : on tourne **dans**
Quantower (voir la stratégie ci-dessus). La branche `credentials.local.json` (modèle
`credentials.local.example.json`, gitignoré) reste câblée pour mémoire mais **n'est pas
utilisée** — inutile puisque la connexion vit déjà dans la plateforme.
