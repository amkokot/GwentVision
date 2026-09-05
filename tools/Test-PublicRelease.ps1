[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$manifestPath = Join-Path $root 'tests/recording-validation/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 2 -or $manifest.Anonymization.Status -ne 'anonymized' -or [string]::IsNullOrWhiteSpace($manifest.Anonymization.Version)) {
    throw 'The public corpus must use schema 2 with an anonymization declaration.'
}
if ($manifest.Files.Count -ne $manifest.FileCount -or $manifest.FileCount -lt 1000) { throw 'The public corpus manifest is incomplete.' }

$caseRoot = Join-Path $root 'tests/recording-validation/cases'
foreach ($caseManifestPath in @(Get-ChildItem -LiteralPath $caseRoot -Filter case.json -Recurse -File)) {
    $case = Get-Content -LiteralPath $caseManifestPath.FullName -Raw | ConvertFrom-Json
    $caseFolder = $caseManifestPath.Directory.Name
    if ($case.SchemaVersion -ne 1 -or $case.Id -cne $caseFolder -or $case.Id -cnotmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') {
        throw "Invalid regression case ID or schema: $caseFolder"
    }
    if ($case.Anonymization.Status -ne 'anonymized' -or [string]::IsNullOrWhiteSpace($case.Anonymization.Version) -or @($case.Anonymization.ProtectedRegions).Count -eq 0) {
        throw "Regression case lacks a reviewed anonymization declaration: $caseFolder"
    }
    if (@($case.Files).Count -eq 0) { throw "Regression case contains no evidence: $caseFolder" }
    foreach ($entry in $case.Files) {
        if ([IO.Path]::GetFileName([string]$entry.File) -cne [string]$entry.File -or [IO.Path]::GetExtension([string]$entry.File).ToLowerInvariant() -notin '.jpg', '.png') {
            throw "Regression evidence must be a JPG or PNG directly inside $caseFolder."
        }
    }
}

$libraryPath = Join-Path $root 'cache/deck-library.json'
$library = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
$privateDeck = @($library.Records | Where-Object {
    $occurrences = if ($null -eq $_.Deck.Occurrences) { @() } else { @($_.Deck.Occurrences) }
    $_.CustomName -or ([string]$_.Deck.SourceUri) -notmatch '^https://www\.playgwent\.com/' -or
    @($occurrences | Where-Object { $null -eq $_ -or $_.Kind -ne 'Import' -or $_.Source -match '(?i)[A-Z]:\\|/Users/|/home/' }).Count -gt 0
})
if ($privateDeck.Count -gt 0) { throw "Deck library contains $($privateDeck.Count) local, custom, or encounter-derived records." }

$forbidden = @(
    'cache/settings.json', 'cache/opponent-memory.json', 'cache/opponent-memory.json.bak',
    'cache/deck-editor-drafts.json', 'cache/workbook-index.json', 'cache/recognition-features',
    'deck-scans', 'diagnostics', 'snapshots', 'pinned-views'
)
foreach ($relative in $forbidden) {
    if (Test-Path -LiteralPath (Join-Path $root $relative)) { throw "Private runtime path is present: $relative" }
}
$unexpectedSessionFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'sessions') -Recurse -File | Where-Object Extension -notin '.jpg', '.png')
if ($unexpectedSessionFiles.Count -gt 0) { throw 'Session journals or non-media files are present in the public corpus.' }

$textExtensions = @('.cs', '.xaml', '.csproj', '.props', '.targets', '.ps1', '.md', '.json', '.yml', '.yaml', '.config', '.sln')
$personalPaths = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|sessions)[\\/]' -and $textExtensions -contains $_.Extension.ToLowerInvariant()
} | Select-String -Pattern '(?i)[A-Z]:\\(?:Users|Program Files|Windows)\\[A-Za-z0-9 ._()-]+|/Users/[A-Za-z0-9._-]+/|/home/[A-Za-z0-9._-]+/'
if ($personalPaths) { throw "A local user path remains in public text: $($personalPaths[0].Path):$($personalPaths[0].LineNumber)" }
Write-Host "PASS public release privacy: schema-2 corpus, $($library.Records.Count) public decks, no runtime-private paths."
