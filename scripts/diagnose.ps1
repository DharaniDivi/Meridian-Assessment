$ErrorActionPreference = 'Continue'
$out = 'C:\AA-PROJECT\diagnose-output.txt'
Remove-Item $out -ErrorAction SilentlyContinue

function Log($msg) { $msg | Tee-Object -FilePath $out -Append }

$base = 'https://ca-seassessment-api-dev.happywater-190f264d.northcentralus.azurecontainerapps.io'
$key = 'sa_90291610bea54baafd7c03676b33c31380315ea49c5289de4ed5955b27d0fe00'
$datasetPath = 'C:\AA-PROJECT\src\Assessment.Api\data\dataset.bin'

Log '=== DATASET.BIN ==='
if (Test-Path $datasetPath) {
    $bytes = [IO.File]::ReadAllBytes($datasetPath)
    Log "Size: $($bytes.Length)"
    $take = [Math]::Min(64, $bytes.Length)
    Log ('Hex64: ' + (($bytes[0..($take-1)] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))
    $preview = [Text.Encoding]::UTF8.GetString($bytes[0..([Math]::Min(300, $bytes.Length-1))])
    Log ('Utf8: ' + ($preview -replace '[^\x20-\x7E]','.'))
} else {
    Log 'dataset.bin NOT FOUND'
}

Log ''
Log '=== API PROBES (Bearer) ==='
$paths = @(
    '/api/v1/key',
    '/api/v1/keys',
    '/api/v1/decryption-key',
    '/api/v1/layer2/key',
    '/api/v1/dataset/key',
    '/api/v1/crypto/key'
)

foreach ($path in $paths) {
    Log "--- GET $path ---"
    try {
        $r = Invoke-WebRequest -Uri ($base + $path) -Headers @{ Authorization = "Bearer $key" } -UseBasicParsing
        Log "Status: $($r.StatusCode)"
        Log "Content-Type: $($r.Headers['Content-Type'])"
        $body = $r.Content
        if ($body.Length -gt 500) { $body = $body.Substring(0, 500) + '...' }
        Log "Body: $body"
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            Log "Status: $([int]$resp.StatusCode)"
            $stream = $resp.GetResponseStream()
            $reader = New-Object IO.StreamReader($stream)
            Log "Body: $($reader.ReadToEnd())"
        } else {
            Log "Error: $($_.Exception.Message)"
        }
    }
}

Log ''
Log 'DONE'
