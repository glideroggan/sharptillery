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

dotnet pack -c Release .\src\SharpArtillery\SharpArtillery.csproj
if errorlevel 1 exit /b %errorlevel%

dotnet nuget push .\src\SharpArtillery\nupkg\nyviken-sharptillery.0.3.6.nupkg --api-key %NUGET_API_KEY% --source https://api.nuget.org/v3/index.json