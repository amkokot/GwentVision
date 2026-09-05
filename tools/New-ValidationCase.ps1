[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CaseId,
    [Parameter(Mandatory = $true)][string]$Summary,
    [Parameter(Mandatory = $true)][string[]]$MediaPath,
    [string]$AddedIn = 'next',
    [switch]$ResultScreen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

if ($CaseId -cnotmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') {
    throw 'CaseId must be lowercase kebab-case and begin with a letter, for example tooltip-cleaver-false-positive.'
}
if ([string]::IsNullOrWhiteSpace($Summary)) { throw 'Summary cannot be empty.' }

$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$casesRoot = Join-Path $project 'tests/recording-validation/cases'
$caseRoot = Join-Path $casesRoot $CaseId
$words = $CaseId.Split('-') | ForEach-Object { [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($_) }
$className = ($words -join '') + 'RegressionCase'
$testPath = Join-Path $project "tests/GwentCompanion.Tests/RecordingCases/$className.cs"
if (Test-Path -LiteralPath $caseRoot) { throw "Validation case already exists: $caseRoot" }
if (Test-Path -LiteralPath $testPath) { throw "Validation test already exists: $testPath" }

$sources = @($MediaPath | ForEach-Object {
    $path = [IO.Path]::GetFullPath($_)
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence file not found: $path" }
    if ([IO.Path]::GetExtension($path).ToLowerInvariant() -notin '.jpg', '.jpeg', '.png', '.bmp') {
        throw "Evidence must be an image: $path"
    }
    $path
})
if ($sources.Count -eq 0) { throw 'At least one evidence image is required.' }

$standardMasks = @(
    [pscustomobject]@{ Name = 'top-left player identity'; X = 0; Y = 0; Width = .17; Height = .13 },
    [pscustomobject]@{ Name = 'bottom-left player identity'; X = 0; Y = .875; Width = .22; Height = .125 }
)
$resultMasks = @(
    [pscustomobject]@{ Name = 'left result identity'; X = .045; Y = .42; Width = .37; Height = .22 },
    [pscustomobject]@{ Name = 'right result identity'; X = .585; Y = .42; Width = .37; Height = .22 },
    [pscustomobject]@{ Name = 'result notification identity'; X = .735; Y = .035; Width = .265; Height = .20 }
)
$protectedRegions = @($standardMasks.Name)
if ($ResultScreen) { $protectedRegions += $resultMasks.Name }
$jpegCodec = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg' | Select-Object -First 1
$temporaryRoot = Join-Path $casesRoot ('.' + $CaseId + '.tmp-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$entries = [Collections.Generic.List[object]]::new()

try {
    for ($index = 0; $index -lt $sources.Count; $index++) {
        $name = 'evidence-{0:D2}.jpg' -f ($index + 1)
        $destination = Join-Path $temporaryRoot $name
        $bitmap = $null; $graphics = $null; $brush = $null
        try {
            $bitmap = [Drawing.Bitmap]::new($sources[$index])
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 8, 10, 12))
            $masks = @($standardMasks)
            if ($ResultScreen) { $masks += $resultMasks }
            foreach ($mask in $masks) {
                $rectangle = [Drawing.Rectangle]::new(
                    [Math]::Floor($bitmap.Width * $mask.X), [Math]::Floor($bitmap.Height * $mask.Y),
                    [Math]::Ceiling($bitmap.Width * $mask.Width), [Math]::Ceiling($bitmap.Height * $mask.Height))
                $graphics.FillRectangle($brush, $rectangle)
            }
            $parameters = [Drawing.Imaging.EncoderParameters]::new(1)
            $quality = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality, [long]96)
            try { $parameters.Param[0] = $quality; $bitmap.Save($destination, $jpegCodec, $parameters) }
            finally { $quality.Dispose(); $parameters.Dispose() }
        }
        finally {
            if ($null -ne $brush) { $brush.Dispose() }
            if ($null -ne $graphics) { $graphics.Dispose() }
            if ($null -ne $bitmap) { $bitmap.Dispose() }
        }
        $info = [IO.FileInfo]::new($destination)
        $entries.Add([ordered]@{
            File = $name
            Bytes = $info.Length
            Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            Tags = @('regression', $CaseId)
        })
    }

    [ordered]@{
        SchemaVersion = 1
        Id = $CaseId
        Summary = $Summary.Trim()
        AddedIn = $AddedIn
        Anonymization = [ordered]@{
            Status = 'anonymized'
            Version = 'identity-mask-v1'
            ProtectedRegions = $protectedRegions
        }
        Files = $entries
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $temporaryRoot 'case.json') -Encoding utf8

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($testPath)) | Out-Null
    $firstEvidence = [string]$entries[0].File
    @"
internal sealed class $className : IRecordingValidationCase
{
    public string Id => "$CaseId";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var evidence = definition.Load("$firstEvidence");
        _ = evidence;
        throw new NotImplementedException("Replace this line with the detector assertion for $CaseId.");
    }
}
"@ | Set-Content -LiteralPath $testPath -Encoding utf8
    [IO.Directory]::Move($temporaryRoot, $caseRoot)
}
catch {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    if (!(Test-Path -LiteralPath $caseRoot) -and (Test-Path -LiteralPath $testPath)) { Remove-Item -LiteralPath $testPath -Force }
    throw
}

Write-Host "Created anonymized validation case: tests/recording-validation/cases/$CaseId"
Write-Host "Implement the assertion in: tests/GwentCompanion.Tests/RecordingCases/$className.cs"
Write-Warning 'Open every generated evidence image and check it for names or identifiers outside the standard masks before committing.'
Write-Host 'Then run: dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release -- --recording-validation-regression'
