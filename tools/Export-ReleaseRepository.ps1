[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'release/GwentVision'),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $project 'release')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$target = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (!$target.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must remain inside the project release directory.' }
if (Test-Path -LiteralPath $target) {
    if (!$Force) { throw "Destination already exists: $target. Pass -Force to replace this verified release-only path." }
    Remove-Item -LiteralPath $target -Recurse -Force
}
[IO.Directory]::CreateDirectory($target) | Out-Null

function Copy-ProjectFile([string]$relative) {
    $sourcePath = Join-Path $project $relative
    $targetPath = Join-Path $target $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath)) | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $targetPath
}
function Copy-ProjectTree([string]$relative, [scriptblock]$include) {
    $sourceRoot = Join-Path $project $relative
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        $local = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
        if (& $include $file $local) {
            $targetPath = Join-Path (Join-Path $target $relative) $local
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $targetPath
        }
    }
}

foreach ($file in @('GwentCompanion.sln', 'Directory.Build.props', 'Directory.Build.targets', 'README.md',
    'CONTRIBUTING.md', 'SECURITY.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'RELEASE_CHECKLIST.md', '.gitattributes')) { Copy-ProjectFile $file }
Copy-ProjectTree '.github' { param($file, $local) $true }
Copy-ProjectTree 'docs' { param($file, $local) $true }
Copy-ProjectTree 'src' { param($file, $local) $local -notmatch '(^|[\\/])(bin|obj)([\\/]|$)' }
Copy-ProjectTree 'assets' { param($file, $local) $true }
Copy-ProjectTree 'tests/GwentCompanion.Tests' { param($file, $local) $local -notmatch '(^|[\\/])(bin|obj)([\\/]|$)' }
foreach ($file in @('tests/board-recovery-training.json', 'tests/vision-fixtures.json', 'tests/vision-training-v0.1.14.json')) { Copy-ProjectFile $file }
Copy-ProjectTree 'tests/recording-validation/cases' { param($file, $local) $true }
foreach ($file in @('tools/Export-AnonymizedValidationCorpus.ps1', 'tools/Export-PublicDeckLibrary.ps1',
    'tools/Export-ReleaseRepository.ps1', 'tools/New-ValidationCase.ps1', 'tools/New-ValidationTest.ps1',
    'tools/Test-PublicRelease.ps1')) { Copy-ProjectFile $file }
foreach ($file in @('cache/gwent-one-cards.json', 'cache/create-point-profiles.json')) { Copy-ProjectFile $file }
foreach ($folder in @('cache/portraits', 'cache/premium-frames', 'cache/observed-art')) {
    if (Test-Path -LiteralPath (Join-Path $project $folder)) { Copy-ProjectTree $folder { param($file, $local) $true } }
}

& (Join-Path $PSScriptRoot 'Export-PublicDeckLibrary.ps1') -SourcePath (Join-Path $project 'cache/deck-library.json') -DestinationPath (Join-Path $target 'cache/deck-library.json')
& (Join-Path $PSScriptRoot 'Export-AnonymizedValidationCorpus.ps1') -SourceRoot $project -DestinationRoot $target

@'
**/bin/
**/obj/
.vs/
.packages/
.build-tools/
artifacts/
release/
snapshots/
pinned-views/
deck-scans/
diagnostics/
cache/decks/
cache/recognition-features/
cache/premium/
cache/video-scan/
cache/playgwent-browser/
cache/observed-decks/
cache/opponent-memory*.json*
cache/settings.json
cache/deck-editor-drafts.json
cache/workbook-index.json
*.user
*.suo
'@ | Set-Content -LiteralPath (Join-Path $target '.gitignore') -Encoding utf8
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $target 'NuGet.Config') -Encoding utf8

& (Join-Path $PSScriptRoot 'Test-PublicRelease.ps1') -RepositoryRoot $target
Write-Host "Release repository assembled at $target"
