param(
    [Parameter(Mandatory=$true)][ValidateSet('x64','x86')][string]$Platform,
    [Parameter(Mandatory=$true)][string]$RefName,
    [string]$SourceRoot='.'
)
$ErrorActionPreference='Stop'
$spec=ConvertFrom-Json '{"Docs":["README.md","README.txt","LICENSE","plugin.json"],"Version":"1.0.4","Host":"1.2.2","Dll":"NicoJkPlugin.dll","Project":"NicoJkPlugin","Framework":"net8.0-windows","Solution":"NicoJkPlugin.sln","Name":"NicoJkPlugin"}'
$root=(Resolve-Path -LiteralPath $SourceRoot).Path
[xml]$project=Get-Content -LiteralPath (Join-Path $root "$($spec.Project)/$($spec.Project).csproj")
if($spec.Name -eq 'AI-rhythm') {
    [xml]$versionProps=Get-Content -LiteralPath (Join-Path $root 'AIrhythm.Version.props')
    $version=[string]$versionProps.Project.PropertyGroup.AIrhythmVersion
} else { $version=[string]$project.Project.PropertyGroup[0].Version }
if(!$version -or $RefName -cne "v$version"){throw 'Tag and product version must match'}
$bin=Join-Path $root "$($spec.Project)/bin/$Platform/Release/$($spec.Framework)"
$dll=Join-Path $bin $spec.Dll
if(!(Test-Path -LiteralPath $dll)){throw 'Plugin DLL missing'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne "$version.0"){throw 'DLL version differs'}
if((Get-FileHash (Join-Path $root 'README.md')).Hash -ne (Get-FileHash (Join-Path $root 'README.txt')).Hash){throw 'README files differ'}
$out="package/$($spec.Name)_v${version}_${Platform}"
if(Test-Path -LiteralPath $out){throw 'Package staging already exists. Use a clean checkout.'}
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $out
foreach($doc in $spec.Docs){Copy-Item -LiteralPath (Join-Path $root $doc) -Destination $out}
if($spec.Name -in @('AI-rhythm','AIrCon')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SDK_LICENSE.txt') -Destination $out
}
$deploy=$spec.Dll
if($spec.Name -eq 'NicoJkPlugin') {
    foreach($file in @('System.Text.Encoding.CodePages.dll','NicoJkPlugin.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $bin $file) -Destination $out
    }
    $nugetRoot=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}
    $encodingPackage=Join-Path $nugetRoot 'system.text.encoding.codepages/8.0.0'
    Copy-Item -LiteralPath (Join-Path $encodingPackage 'LICENSE.TXT') -Destination (Join-Path $out 'ENCODING_LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $encodingPackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $out 'ENCODING_THIRD_PARTY_NOTICES.txt')
    $manifest=Get-Content -LiteralPath (Join-Path $out 'plugin.json') -Raw | ConvertFrom-Json
    if($manifest.version -ne $version -or $manifest.entry -ne $spec.Dll){throw 'Manifest differs from product'}
    $deploy='NicoJkPlugin.dll、plugin.json、NicoJkPlugin.deps.json、System.Text.Encoding.CodePages.dll'
}
@"
$($spec.Name) v$version ($Platform) - ビルド済み配布

このZIPはビルド済みです。Visual Studioによるビルドは不要です。
TvAIr $($spec.Host)以上と、TvAIrと同じアーキテクチャの配布物を使用してください。
TvAIrを終了してから、$deploy をTvAIr.exeと同じフォルダーのPluginsへ配置してください。
TvAIrを起動し、プラグインメニューに表示されることを確認してください。
更新時もTvAIrを終了して、同じファイルを置き換えます。設定や履歴は削除しないでください。
設定・使用方法はREADMEを参照してください。
ライセンス文書は配布物の利用条件です。SDK_LICENSE.txtがある場合は同梱SDKの利用条件を示します。
"@ | Set-Content -LiteralPath (Join-Path $out 'INSTALL.txt') -Encoding UTF8
Compress-Archive -Path "$out/*" -DestinationPath "package/$($spec.Name)_v${version}_${Platform}.zip"