# make-zips.ps1 -- build the two distribution zips into dist\
# (ASCII only: Windows PowerShell reads BOM-less .ps1 as ANSI, and a UTF-8
#  em-dash misdecodes into a curly quote that breaks string parsing.)
#
#   truedat.zip         full bundle: the dist\truedat\ folder (exe + tools + docs),
#                       zipped WITH the folder so it extracts to truedat\
#   truedat_latest.zip  lightweight updater: truedat.exe + README.md + SBOM.md +
#                       LICENSE at the zip root (drop-in over an existing install)
#
# Both zips always include README.md, SBOM.md and LICENSE.
# Run after build-truedat.cmd. The exe's version stamp is printed first so a
# stale-exe zip is caught by eye before it ships (the tag-the-build lesson).
#
# -OutDir writes the zips somewhere else (e.g. a scratch dir for a dry run);
# default is this script's own folder, i.e. dist\.

param([string]$OutDir = $PSScriptRoot)

$ErrorActionPreference = 'Stop'

$bundleDir = Join-Path $PSScriptRoot 'truedat'
$exe       = Join-Path $bundleDir 'truedat.exe'
$docs      = @('README.md', 'SBOM.md', 'LICENSE')

if (!(Test-Path $exe)) {
    Write-Error "not found: $exe -- build first (build-truedat.cmd)"
}
foreach ($doc in $docs) {
    if (!(Test-Path (Join-Path $bundleDir $doc))) {
        Write-Error "missing $doc in $bundleDir -- the zips must carry the docs"
    }
}
if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

Write-Host "exe:     $exe"
Write-Host "version: $(& $exe --version)"
Write-Host ""

$fullZip   = Join-Path $OutDir 'truedat.zip'
$latestZip = Join-Path $OutDir 'truedat_latest.zip'

Write-Host "building $fullZip (full bundle) ..."
Compress-Archive -Path $bundleDir -DestinationPath $fullZip -CompressionLevel Optimal -Force

Write-Host "building $latestZip (exe + docs) ..."
$latestFiles = @($exe) + ($docs | ForEach-Object { Join-Path $bundleDir $_ })
Compress-Archive -Path $latestFiles -DestinationPath $latestZip -CompressionLevel Optimal -Force

Write-Host ""
Get-Item $fullZip, $latestZip | ForEach-Object {
    Write-Host ("{0,-20} {1,14:N0} bytes   {2}" -f $_.Name, $_.Length, $_.LastWriteTime)
}
