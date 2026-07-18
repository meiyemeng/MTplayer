param(
    [string]$Version = "1.3.0",
    [string]$Runtime = "osx-x64"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$publish = Join-Path $root "artifacts\macos\publish-$Runtime"
$staging = Join-Path $root "artifacts\macos\bundle"
$app = Join-Path $staging "MTPlayer.app"
$macos = Join-Path $app "Contents\MacOS"
$resources = Join-Path $app "Contents\Resources"

if (-not (Test-Path (Join-Path $publish "MTPlayer"))) {
    throw "macOS publish output is missing. Run dotnet publish first."
}

if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $macos, $resources | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $macos -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Info.plist") -Destination (Join-Path $app "Contents\Info.plist") -Force
Copy-Item -LiteralPath (Join-Path $root "src\MTPlayer.Mac\Assets\mtplayer.icns") -Destination (Join-Path $resources "mtplayer.icns") -Force

$plist = Get-Content -LiteralPath (Join-Path $app "Contents\Info.plist") -Raw
$plist = $plist.Replace("<string>1.3.0</string>", "<string>$Version</string>")
Set-Content -LiteralPath (Join-Path $app "Contents\Info.plist") -Value $plist -Encoding utf8

Write-Output $app
