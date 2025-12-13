dotnet publish -c Release ./src/SharpArtillery/SharpArtillery.csproj -o ./src/SharpArtillery/nupkg
REM -p:PublishSingleFile=true

dotnet nuget push .\src\SharpArtillery\nupkg\nyviken-sharptillery.0.3.*.nupkg `
  --api-key $env:NUGET_API_KEY `
  --source https://api.nuget.org/v3/index.json