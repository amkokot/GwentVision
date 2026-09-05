[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$DestinationRoot,
    [ValidateRange(85, 100)][int]$JpegQuality = 96
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$sourceSessions = [IO.Path]::GetFullPath((Join-Path $source 'sessions')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$destinationSessions = [IO.Path]::GetFullPath((Join-Path $destination 'sessions')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $source 'tests/recording-validation/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1 -and $manifest.SchemaVersion -ne 2) { throw 'Unsupported source corpus manifest schema.' }

$standardMasks = @(
    [pscustomobject]@{ Name = 'top-left player identity'; X = 0; Y = 0; Width = .17; Height = .13 },
    [pscustomobject]@{ Name = 'bottom-left player identity'; X = 0; Y = .875; Width = .22; Height = .125 }
)
$resultMasks = @(
    [pscustomobject]@{ Name = 'left result identity'; X = .045; Y = .42; Width = .37; Height = .22 },
    [pscustomobject]@{ Name = 'right result identity'; X = .585; Y = .42; Width = .37; Height = .22 },
    [pscustomobject]@{ Name = 'result notification identity'; X = .735; Y = .035; Width = .265; Height = .20 }
)
$jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg' | Select-Object -First 1
$outputFiles = [Collections.Generic.List[object]]::new()
$totalBytes = [long]0
$index = 0

foreach ($entry in $manifest.Files) {
    $relative = ([string]$entry.File).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $source $relative))
    $destinationPath = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$sourcePath.StartsWith($sourceSessions, [StringComparison]::OrdinalIgnoreCase)) { throw "Source path escapes sessions: $($entry.File)" }
    if (!$destinationPath.StartsWith($destinationSessions, [StringComparison]::OrdinalIgnoreCase)) { throw "Destination path escapes sessions: $($entry.File)" }
    if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing source corpus file: $($entry.File)" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
    $temporary = $destinationPath + '.tmp'
    $bitmap = $null
    $graphics = $null
    $brush = $null
    try {
        $bitmap = [Drawing.Bitmap]::new($sourcePath)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 8, 10, 12))
        $masks = [Collections.Generic.List[object]]::new()
        foreach ($mask in $standardMasks) { $masks.Add($mask) }
        if (@($entry.Tags | Where-Object { $_ -match 'result|mmr' }).Count -gt 0) {
            foreach ($mask in $resultMasks) { $masks.Add($mask) }
        }
        foreach ($mask in $masks) {
            $rectangle = [Drawing.Rectangle]::new(
                [Math]::Floor($bitmap.Width * $mask.X),
                [Math]::Floor($bitmap.Height * $mask.Y),
                [Math]::Ceiling($bitmap.Width * $mask.Width),
                [Math]::Ceiling($bitmap.Height * $mask.Height))
            $graphics.FillRectangle($brush, $rectangle)
        }
        if ([IO.Path]::GetExtension($destinationPath) -ieq '.png') {
            $bitmap.Save($temporary, [Drawing.Imaging.ImageFormat]::Png)
        }
        else {
            $parameters = [Drawing.Imaging.EncoderParameters]::new(1)
            $quality = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality, [long]$JpegQuality)
            try { $parameters.Param[0] = $quality; $bitmap.Save($temporary, $jpegCodec, $parameters) }
            finally { $quality.Dispose(); $parameters.Dispose() }
        }
    }
    finally {
        if ($null -ne $brush) { $brush.Dispose() }
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }
    [IO.File]::Move($temporary, $destinationPath, $true)
    $info = [IO.FileInfo]::new($destinationPath)
    $hash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
    $outputFiles.Add([ordered]@{ File = ([string]$entry.File).Replace('\', '/'); Bytes = $info.Length; Sha256 = $hash; Tags = @($entry.Tags) })
    $totalBytes += $info.Length
    $index++
    if ($index % 100 -eq 0 -or $index -eq $manifest.Files.Count) { Write-Progress -Activity 'Anonymizing validation corpus' -Status "$index / $($manifest.Files.Count)" -PercentComplete (100 * $index / $manifest.Files.Count) }
}

$publicManifest = [ordered]@{
    SchemaVersion = 2
    SourceAudit = 'private source audit retained outside the public repository'
    Policy = 'Reviewed detector regressions with irreversible player-identity masks. Historical journals are not labels and are excluded.'
    Anonymization = [ordered]@{
        Status = 'anonymized'
        Version = 'identity-mask-v1'
        ProtectedRegions = @('top-left player identity', 'bottom-left player identity', 'result identity band', 'result notification identity')
    }
    FileCount = $outputFiles.Count
    Bytes = $totalBytes
    Files = $outputFiles
}
$publicManifestPath = Join-Path $destination 'tests/recording-validation/manifest.json'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($publicManifestPath)) | Out-Null
$publicManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $publicManifestPath -Encoding utf8
Write-Progress -Activity 'Anonymizing validation corpus' -Completed
Write-Host "Exported $($outputFiles.Count) anonymized corpus files ($totalBytes bytes)."
