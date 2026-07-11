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

# TFM auto (net8.0 pour v1.145.x, net10.0 pour v1.146.14+) : prendre le build le plus récent.
$out = Get-ChildItem (Join-Path $PSScriptRoot "bin\Release") -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "NqTickExtractor.dll") } |
    Sort-Object { (Get-Item (Join-Path $_.FullName "NqTickExtractor.dll")).LastWriteTime } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$dest = Join-Path $latest.FullName "..\..\..\Settings\Scripts\Strategies\NqTickExtractor"
$dest = [System.IO.Path]::GetFullPath($dest)
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "NqTickExtractor.dll","NqTickExtractor.deps.json","NqTickExtractor.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Déployé dans : $dest (depuis $out)"
Write-Host "Dans Quantower, panneau Strategies :"
Write-Host "  - NQ Tick Extractor : ticks (fenêtre ~2-3 semaines) -> NQ-<contrat>.db"
Write-Host "  - NQ Bars Extractor : barres minute (toute la profondeur serveur) -> NQ-<contrat>-1m.db"
