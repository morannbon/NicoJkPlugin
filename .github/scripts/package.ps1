param(
    [Parameter(Mandatory = $true)]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$RefName
)

$ErrorActionPreference = "Stop"

$source = "NicoJkPlugin\bin\$Platform\Release\net8.0-windows"
$out = "package\NicoJkPlugin_${RefName}_${Platform}"
$zip = "package\NicoJkPlugin_${Platform}.zip"

if (-not (Test-Path $source)) {
    throw "Build output not found: $source"
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item "$source\*" $out -Recurse -Force

Copy-Item "README.md" $out
Copy-Item "README.txt" $out
Copy-Item "LICENSE" $out

Get-ChildItem $out -Recurse -Include *.pdb,*.lib,*.exp |
    Remove-Item -Force

Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
