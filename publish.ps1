# Publish script for Sharptillery
$ErrorActionPreference = "Stop"

# Load NUGET_API_KEY from environment or .env file
if (-not $env:NUGET_API_KEY) {
    if (Test-Path ".env") {
        Get-Content ".env" | ForEach-Object {
            if ($_ -match "^NUGET_API_KEY=(.+)$") {
                $env:NUGET_API_KEY = $matches[1]
            }
        }
    }
}

if (-not $env:NUGET_API_KEY) {
    Write-Error "Error: NUGET_API_KEY is not set. Set it in your shell or create a .env file with: NUGET_API_KEY=..."
    exit 1
}

# Extract version from csproj
$csproj = "src\SharpArtillery\SharpArtillery.csproj"
$versionMatch = Select-String -Path $csproj -Pattern '<Version>(.+)</Version>'
if (-not $versionMatch) {
    Write-Error "Error: Could not extract version from $csproj"
    exit 1
}
$version = $versionMatch.Matches.Groups[1].Value
Write-Host "Building version $version..." -ForegroundColor Cyan

# Pack
dotnet pack -c Release $csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Push
$nupkg = "src\SharpArtillery\nupkg\nyviken-sharptillery.$version.nupkg"
Write-Host "Pushing $nupkg..." -ForegroundColor Cyan
dotnet nuget push $nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json

if ($LASTEXITCODE -eq 0) {
    Write-Host "Successfully published version $version!" -ForegroundColor Green
}
