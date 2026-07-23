param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "0.1.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\.."))
$projectPath = Join-Path $repositoryRoot "src\GlujDrive.Server\GlujDrive.Server.csproj"
$launcherSource = Join-Path $repositoryRoot "packaging\windows"
$innoScript = Join-Path $launcherSource "GlujDrive.iss"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$stagingRoot = Join-Path $artifactsRoot "staging"
$releaseRoot = Join-Path $artifactsRoot "release"
$installerPublish = Join-Path $stagingRoot "installer-win-x64"
$portablePublish = Join-Path $stagingRoot "portable-win-x64"

if (-not $artifactsRoot.StartsWith(
        $repositoryRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The artifacts directory resolved outside the repository."
}

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $installerPublish, $portablePublish, $releaseRoot |
    Out-Null

Write-Host "Publishing self-contained Windows installer payload..."
dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $installerPublish `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "The self-contained publish failed."
}

if (Test-Path -LiteralPath (Join-Path $installerPublish "data")) {
    throw "Runtime user data was included in the installer payload. Release aborted."
}

Write-Host "Publishing framework-dependent portable payload..."
dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $portablePublish `
    -p:Version=$Version `
    -p:SkipFrontendBuild=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "The portable publish failed."
}

if (Test-Path -LiteralPath (Join-Path $portablePublish "data")) {
    throw "Runtime user data was included in the portable payload. Release aborted."
}

$installerWwwroot = Join-Path $installerPublish "wwwroot"
$portableWwwroot = Join-Path $portablePublish "wwwroot"
if (-not (Test-Path -LiteralPath $installerWwwroot)) {
    throw "The production React build was not included in the publish output."
}

Copy-Item -LiteralPath $installerWwwroot -Destination $portableWwwroot -Recurse

foreach ($publishDirectory in @($installerPublish, $portablePublish)) {
    Copy-Item `
        -LiteralPath (Join-Path $launcherSource "Start-GlujDrive.cmd") `
        -Destination $publishDirectory
    Copy-Item `
        -LiteralPath (Join-Path $launcherSource "Open-GlujDriveWhenReady.ps1") `
        -Destination $publishDirectory
}

$portableArchive = Join-Path $releaseRoot "GlujDrive-Portable-$Version-win-x64.zip"
Write-Host "Creating $portableArchive..."
Compress-Archive `
    -Path (Join-Path $portablePublish "*") `
    -DestinationPath $portableArchive `
    -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $innoCompiler = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty Source

    if (-not $innoCompiler) {
        $innoCandidates = @(
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
        )
        $innoCompiler = $innoCandidates |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
            Select-Object -First 1
    }

    if (-not $innoCompiler) {
        throw "Inno Setup 6 was not found. Install it or rerun with -SkipInstaller."
    }

    Write-Host "Compiling the Inno Setup installer..."
    & $innoCompiler `
        "/DMyAppVersion=$Version" `
        "/DPublishDir=$installerPublish" `
        "/DOutputDir=$releaseRoot" `
        $innoScript

    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed."
    }
}

$checksumPath = Join-Path $releaseRoot "SHA256SUMS.txt"
$releaseFiles = Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object Name -ne "SHA256SUMS.txt" |
    Sort-Object Name

$checksums = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}

Set-Content -LiteralPath $checksumPath -Value $checksums -Encoding ascii

Write-Host ""
Write-Host "Windows release artifacts:"
Get-ChildItem -LiteralPath $releaseRoot -File |
    Select-Object Name, Length, LastWriteTime
