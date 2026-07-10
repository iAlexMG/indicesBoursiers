# Compile le POC en résolvant dynamiquement le dossier Quantower v* le plus récent.
$root = "C:\Quantower\TradingPlatform"
$latest = Get-ChildItem $root -Directory -Filter "v*" |
    Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
    Select-Object -First 1
if ($null -eq $latest) { throw "Aucun dossier v* sous $root" }
Write-Host "Quantower bin : $($latest.FullName)\bin"
dotnet build "$PSScriptRoot\Phase0Poc.csproj" -c Release -p:QuantowerBin="$($latest.FullName)\bin"
