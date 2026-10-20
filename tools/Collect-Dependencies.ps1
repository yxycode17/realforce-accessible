param([string]$SourceDirectory = 'C:\Program Files (x86)\Topre\REALFORCE CONNECT Software')
$ErrorActionPreference = 'Stop'
$rfDestination = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'
$rfFiles = @(
    @{ Source = 'RFSWLib.dll'; Name = 'RFSWLib.dll'; Hash = '291fc248d9a9cf0b5e9275b9e4c8dfe2e5c59f60e09e1486b0c6e63e4c642a3e' },
    @{ Source = 'Profile\R30B.r3k'; Name = 'R30B.r3k'; Hash = '58b943ffe4b9749f5ad14afa6eb567897857f3d6a7c2f728391e4a779ae6c0f7' }
)
# Validate all inputs before copying. Nothing is loaded from this location at runtime.
foreach ($rfFile in $rfFiles) {
    $rfPath = Join-Path $SourceDirectory $rfFile.Source
    if ((Get-FileHash -LiteralPath $rfPath -Algorithm SHA256).Hash -ne $rfFile.Hash) {
        throw "Unsupported dependency: $rfPath"
    }
}
New-Item -ItemType Directory -Path $rfDestination -Force | Out-Null
foreach ($rfFile in $rfFiles) {
    Copy-Item -LiteralPath (Join-Path $SourceDirectory $rfFile.Source) -Destination (Join-Path $rfDestination $rfFile.Name) -Force
}
Write-Output 'Dependencies collected into dist. Run build.ps1 next.'
