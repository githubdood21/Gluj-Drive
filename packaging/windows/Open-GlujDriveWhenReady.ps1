$healthUrl = "http://127.0.0.1:5199/api/health"
$browserUrl = "http://localhost:5199"
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
        $response = Invoke-WebRequest `
            -Uri $healthUrl `
            -Method Get `
            -UseBasicParsing `
            -TimeoutSec 1

        if ($response.StatusCode -eq 200) {
            Start-Process $browserUrl
            exit 0
        }
    }
    catch {
        Start-Sleep -Milliseconds 250
    }
}

exit 1
