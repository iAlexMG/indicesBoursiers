# Compile l'indicateur et le deploie dans Settings\Scripts\Indicators de Quantower.
$ErrorActionPreference = "Stop"
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending | Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
$bin = Join-Path $latest.FullName "bin"

dotnet build "$PSScriptRoot\SmaCrossNq.csproj" -c Release -p:QuantowerBin="$bin"

$out  = Join-Path $PSScriptRoot "bin\Release\net8.0"
$dest = [System.IO.Path]::GetFullPath((Join-Path $latest.FullName "..\..\..\Settings\Scripts\Indicators\SmaCrossNq"))
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in "SmaCrossNq.dll","SmaCrossNq.deps.json","SmaCrossNq.pdb") {
    Copy-Item (Join-Path $out $f) $dest -Force
}
Write-Host "Deploye dans : $dest"
Write-Host "Dans Quantower : graphe NQ 1H -> clic droit -> Add indicator -> 'SMA Cross NQ (50/200)'."
