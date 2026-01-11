@echo off
setlocal enableextensions enabledelayedexpansion

REM Optional: load NUGET_API_KEY from a local .env file.
REM Expected format (single line):
REM   NUGET_API_KEY=your-key-here
if "%NUGET_API_KEY%"=="" if exist ".env" (
  for /f "usebackq tokens=1* delims==" %%A in (".env") do (
    if /i "%%A"=="NUGET_API_KEY" set "NUGET_API_KEY=%%B"
  )
)

if "%NUGET_API_KEY%"=="" (
  echo Error: NUGET_API_KEY is not set.
  echo Set it in your shell or create a .env file with: NUGET_API_KEY=...
  exit /b 1
)

REM Extract version from csproj
for /f "tokens=2 delims=<>" %%V in ('findstr /r "<Version>[0-9]" .\src\SharpArtillery\SharpArtillery.csproj') do set "VERSION=%%V"
if "%VERSION%"=="" (
  echo Error: Could not extract version from SharpArtillery.csproj
  exit /b 1
)
echo Building version %VERSION%...

dotnet pack -c Release .\src\SharpArtillery\SharpArtillery.csproj
if errorlevel 1 exit /b %errorlevel%

dotnet nuget push .\src\SharpArtillery\nupkg\nyviken-sharptillery.%VERSION%.nupkg --api-key %NUGET_API_KEY% --source https://api.nuget.org/v3/index.json