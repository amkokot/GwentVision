[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^https://[a-z0-9-]+\.supabase\.co$')][string]$SupabaseUrl,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublishableKey,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9._-]{0,39}$')][string]$Season,
    [Parameter(Mandatory)][ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')][string]$Email,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateSet('live_game', 'stream_archive', 'collaborator_import')][string]$Source,
    [switch]$KeepCompressedPayload,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($OutputPath)
if ((Test-Path -LiteralPath $target) -and !$Force) { throw "Output already exists: $target. Use -Force to replace it." }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
$temporary = $target + '.tmp-' + [Guid]::NewGuid().ToString('N')
$credential = Get-Credential -UserName $Email -Message 'Enter your individual Gwent Vision analyst password.'
$password = $credential.GetNetworkCredential().Password
$token = $null
$writer = $null

function Expand-MatchPayload([object]$row) {
    if ($KeepCompressedPayload -or $row.payloadFormat -ne 'json-gzip-v1') { return $row }
    $bytes = [Convert]::FromBase64String([string]$row.payloadBase64)
    $input = [IO.MemoryStream]::new($bytes, $false)
    $gzip = [IO.Compression.GZipStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
    $reader = [IO.StreamReader]::new($gzip, [Text.UTF8Encoding]::new($false, $true))
    try { $match = ($reader.ReadToEnd() | ConvertFrom-Json) }
    finally { $reader.Dispose(); $gzip.Dispose(); $input.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
    $row.PSObject.Properties.Remove('payloadBase64')
    $row | Add-Member -NotePropertyName match -NotePropertyValue $match
    return $row
}

try {
    $authBody = @{ email = $Email; password = $password } | ConvertTo-Json -Compress
    $session = Invoke-RestMethod -Method Post -Uri "$SupabaseUrl/auth/v1/token?grant_type=password" `
        -Headers @{ apikey = $PublishableKey } -ContentType 'application/json' -Body $authBody
    $token = [string]$session.access_token
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Supabase did not return an access token.' }

    $writer = [IO.StreamWriter]::new($temporary, $false, [Text.UTF8Encoding]::new($false))
    $after = 0L; $count = 0
    do {
        $query = "season=$([Uri]::EscapeDataString($Season))&after=$after&limit=500"
        if ($Source) { $query += "&source=$([Uri]::EscapeDataString($Source))" }
        $page = Invoke-RestMethod -Method Get -Uri "$SupabaseUrl/functions/v1/gw-api/analyst/export?$query" `
            -Headers @{ Authorization = "Bearer $token" }
        $rows = @($page.rows)
        foreach ($row in $rows) {
            $expanded = Expand-MatchPayload $row
            $writer.WriteLine(($expanded | ConvertTo-Json -Depth 20 -Compress))
            $after = [Math]::Max($after, [long]$row.id); $count++
        }
    } while ($rows.Count -eq 500)
    $writer.Flush(); $writer.Dispose(); $writer = $null
    Move-Item -LiteralPath $temporary -Destination $target -Force
    Write-Host "Exported $count private $Season observation(s) to $target. Protect this pseudonymous research file."
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    $authBody = $null; $password = $null; $token = $null; $session = $null; $credential = $null
}
