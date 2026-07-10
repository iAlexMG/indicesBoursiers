# Compile la stratégie de mesure et la déploie dans le dossier Scripts de Quantower.
# Résout dynamiquement le bin v* le plus récent (jamais de chemin en dur).
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\Phase0Strategy.csproj" -c Release -p:QuantowerBin="$bin"

$out  = Join-Path $PSScriptRoot "bin\Release\net8.0"
$dest = Join-Path $latest.FullName "..\..\..\Settings\Scripts\Strategies\Phase0Measure"
$dest = [System.IO.Path]::GetFullPath($dest)
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "Phase0Measure.dll","Phase0Measure.deps.json","Phase0Measure.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Déployé dans : $dest"
Write-Host "Dans Quantower : panneau Strategies → Phase0 Measure (NQ) → choisir le symbole NQ → Run."
