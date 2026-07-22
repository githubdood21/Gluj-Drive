param(
    [Parameter(Mandatory = $true)]
    [string]$ModelDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeDll,

    [string]$OutputDirectory = "src/GlujDrive.Server/ai"
)

$ErrorActionPreference = 'Stop'
$modelRoot = (Resolve-Path -LiteralPath $ModelDirectory).Path
$runtimePath = (Resolve-Path -LiteralPath $RuntimeDll).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$manifestSource = Join-Path $modelRoot 'manifest.json'

if (-not (Test-Path -LiteralPath $manifestSource -PathType Leaf)) {
    throw "The converted model directory does not contain manifest.json."
}

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("gluj-ai-package-" + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $workRoot 'package'
$archivePath = Join-Path $outputRoot 'TinyCLIP-ncnn-win-x64.zip'
$hashPath = "$archivePath.sha256"

try {
    [System.IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    $requiredModelFiles = @(
        'manifest.json',
        'image.param',
        'image.bin',
        'text.param',
        'text.bin',
        'vocab.json',
        'merges.txt',
        'embedding-dimensions.txt'
    )
    foreach ($relativePath in $requiredModelFiles) {
        $sourcePath = Join-Path $modelRoot $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "The converted model is missing $relativePath."
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $packageRoot $relativePath) -Force
    }
    Get-ChildItem -LiteralPath $modelRoot -File -Filter '*LICENSE*' |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $packageRoot $_.Name) -Force
        }
    $runtimeDirectory = Join-Path $packageRoot 'runtime/win-x64'
    [System.IO.Directory]::CreateDirectory($runtimeDirectory) | Out-Null
    Copy-Item -LiteralPath $runtimePath -Destination (Join-Path $runtimeDirectory 'GlujDrive.Inference.Native.dll') -Force

    $manifestPath = Join-Path $packageRoot 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $files = [ordered]@{}
    $packagePrefix = $packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($packagePrefix.Length).Replace('\', '/')
            $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    $manifest.files = $files
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  TinyCLIP-ncnn-win-x64.zip" | Set-Content -LiteralPath $hashPath -Encoding ascii

    Write-Host "Created $archivePath"
    Write-Host "Created $hashPath"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
