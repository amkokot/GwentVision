[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$library = Get-Content -LiteralPath $SourcePath -Raw | ConvertFrom-Json
if ($library.Version -ne 5) { throw 'The release exporter expects deck-library schema 5.' }

$records = [Collections.Generic.List[object]]::new()
foreach ($record in $library.Records) {
    $uri = [string]$record.Deck.SourceUri
    $isPlayGwent = $uri -match '^https?://www\.playgwent\.com/'
    $occurrences = if ($null -eq $record.Deck.Occurrences) { @() } else { @($record.Deck.Occurrences) }
    $isCreated = [string]::IsNullOrWhiteSpace($uri) -and
        ([string]$record.Deck.Id) -match '^(?:manual|builder)-[a-zA-Z0-9]+$' -and
        @($occurrences | Where-Object { $null -ne $_ -and $_.Kind -ne 'LibrarySave' }).Count -eq 0
    if (!$isPlayGwent -and !$isCreated) { continue }
    if ($isPlayGwent -and $uri.StartsWith('http://', [StringComparison]::OrdinalIgnoreCase)) {
        $uri = $uri -replace '^http://', 'https://'
        $record.Deck.SourceUri = $uri
    }
    $record.Aliases = @($record.Deck.Id)
    $record.OriginalNames = @($record.Deck.Name)
    if ($isPlayGwent) { $record.Sources = [string[]]@($uri) }
    else { $record.Sources = [string[]]@() }
    $record.CustomName = $isCreated
    $record.Details = $null
    $record.Export = $null
    $patches = if ($null -eq $record.Deck.Patches) { @() } else { @($record.Deck.Patches) }
    $record.Deck.Occurrences = @($occurrences | Where-Object {
        $null -ne $_ -and $_.Kind -eq $(if ($isPlayGwent) { 'Import' } else { 'LibrarySave' }) -and
        $_.Source -notmatch '(?i)[A-Z]:\\|/Users/|/home/'
    })
    $record.Deck.Patches = @($patches | Where-Object { $null -ne $_ -and
        $_.Source -notmatch '(?i)^Occurrence (?:Opponent|Observed|Encounter)|[A-Z]:\\|/Users/|/home/' })
    $records.Add($record)
}
$kept = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($record in $records) { [void]$kept.Add([string]$record.Fingerprint) }
$groups = [Collections.Generic.List[object]]::new()
foreach ($group in $library.VariationGroups) {
    $members = @($group.Members | Where-Object { $kept.Contains([string]$_) })
    if ($members.Count -eq 0) { continue }
    $first = $records | Where-Object Fingerprint -eq $members[0] | Select-Object -First 1
    $groups.Add([ordered]@{ Id = $group.Id; Name = $first.Deck.Name; Members = $members })
}
$links = @($library.ImportedLinks | Where-Object { ([string]$_.DeckUri) -match '^https?://www\.playgwent\.com/' } | ForEach-Object {
    if (([string]$_.DeckUri).StartsWith('http://', [StringComparison]::OrdinalIgnoreCase))
        { $_.DeckUri = ([string]$_.DeckUri) -replace '^http://', 'https://' }
    $_
})
$public = [ordered]@{
    Version = 5
    Cards = @($library.Cards)
    Records = $records
    ImportedLinks = $links
    VariationGroups = $groups
    VariationPolicy = $library.VariationPolicy
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($DestinationPath))) | Out-Null
$public | ConvertTo-Json -Depth 100 -Compress | Set-Content -LiteralPath $DestinationPath -Encoding utf8
Write-Host "Exported $($records.Count) PlayGWENT or manually created decks; excluded $($library.Records.Count - $records.Count) opponent-derived or unsupported records."
