[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^https://[a-z0-9-]+\.supabase\.co$')][string]$SupabaseUrl,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublishableKey,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9._-]{0,39}$')][string]$Season,
    [Parameter(Mandatory)][ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')][string]$Email,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$InputPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._:/+-]{0,159}$')][string]$SourceLocator,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$')][string]$ProducerVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$namespace = 'gwent-vision-collaborator-match'
$schemaVersion = 1
$credential = Get-Credential -UserName $Email -Message 'Enter your individual collaborator password.'
$password = $credential.GetNetworkCredential().Password
$token = $null

function Send-Batch([System.Collections.Generic.List[object]]$batch) {
    if ($batch.Count -eq 0) { return }
    $body = [ordered]@{
        schema = 1
        batchId = [Guid]::NewGuid().ToString()
        season = $Season
        datasetNamespace = $namespace
        metadataSchemaVersion = $schemaVersion
        producerVersion = $ProducerVersion
        sourceLocator = $SourceLocator
        matches = $batch.ToArray()
    } | ConvertTo-Json -Depth 8 -Compress
    $receipt = Invoke-RestMethod -Method Post `
        -Uri "$SupabaseUrl/functions/v1/gw-api/collaborator/import" `
        -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body
    Write-Host "Receipt $($receipt.receiptId): accepted $($receipt.accepted), unchanged $($receipt.unchanged)."
    $batch.Clear()
}

try {
    $authBody = @{ email = $Email; password = $password } | ConvertTo-Json -Compress
    $session = Invoke-RestMethod -Method Post -Uri "$SupabaseUrl/auth/v1/token?grant_type=password" `
        -Headers @{ apikey = $PublishableKey } -ContentType 'application/json' -Body $authBody
    $token = [string]$session.access_token
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Supabase did not return an access token.' }

    $batch = [System.Collections.Generic.List[object]]::new()
    $encodedBytes = 0
    $lineNumber = 0
    $recordCount = 0
    foreach ($line in [IO.File]::ReadLines([IO.Path]::GetFullPath($InputPath))) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $record = $line | ConvertFrom-Json }
        catch { throw "Line $lineNumber is not valid JSON." }
        if ([string]$record.patch -ne $Season) {
            throw "Line $lineNumber uses patch '$($record.patch)' instead of season '$Season'."
        }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($line)
        if ($bytes.Length -gt 262144) { throw "Line $lineNumber exceeds the 256 KiB record limit." }
        $encoded = [Convert]::ToBase64String($bytes)
        if ($batch.Count -ge 250 -or ($encodedBytes + $encoded.Length) -gt 7340032) {
            Send-Batch $batch
            $encodedBytes = 0
        }
        $batch.Add([ordered]@{ format = 'json-v1'; recordBase64 = $encoded })
        $encodedBytes += $encoded.Length
        $recordCount++
    }
    if ($recordCount -eq 0) { throw 'The input file contains no match records.' }
    Send-Batch $batch
}
finally {
    $authBody = $null; $password = $null; $token = $null; $session = $null; $credential = $null
}
