# Compile l'extracteur et le déploie dans le dossier Scripts de Quantower.
# Résout dynamiquement le bin v* le plus récent (jamais de chemin en dur).
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\NqExtractor.csproj" -c Release -p:QuantowerBin="$bin"

$out  = Join-Path $PSScriptRoot "bin\Release\net8.0"
$dest = Join-Path $latest.FullName "..\..\..\Settings\Scripts\Strategies\NqTickExtractor"
$dest = [System.IO.Path]::GetFullPath($dest)
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "NqTickExtractor.dll","NqTickExtractor.deps.json","NqTickExtractor.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Déployé dans : $dest"
Write-Host "Dans Quantower : panneau Strategies → NQ Tick Extractor → choisir NQ → Start."
