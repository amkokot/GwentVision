[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TestId,
    [Parameter(Mandatory = $true)][string]$Kind,
    [Parameter(Mandatory = $true)][string]$Summary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($value in @($TestId, $Kind)) {
    if ($value -cnotmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') {
        throw 'TestId and Kind must be lowercase kebab-case, for example library-filtering and feature.'
    }
}
if ([string]::IsNullOrWhiteSpace($Summary)) { throw 'Summary cannot be empty.' }

$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$words = $TestId.Split('-') | ForEach-Object { [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($_) }
$className = ($words -join '') + 'ValidationCase'
$testPath = Join-Path $project "tests/GwentCompanion.Tests/ValidationCases/$className.cs"
if (Test-Path -LiteralPath $testPath) { throw "Validation test already exists: $testPath" }

$escapedSummary = $Summary.Trim().Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n')
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($testPath)) | Out-Null
@"
internal sealed class $className : IContributorValidationCase
{
    public string Id => "$TestId";
    public string Kind => "$Kind";
    public string Summary => "$escapedSummary";

    public Task RunAsync(ContributorValidationContext context)
    {
        _ = context;
        throw new NotImplementedException("Replace this line with a deterministic assertion for $TestId.");
    }
}
"@ | Set-Content -LiteralPath $testPath -Encoding utf8

Write-Host "Created $Kind validation test: tests/GwentCompanion.Tests/ValidationCases/$className.cs"
Write-Host 'Implement the assertion, then run: dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release -- --contributor-validation-regression'
