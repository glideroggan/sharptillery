#Requires -Version 7
<#
.SYNOPSIS
    Benchmark gate: short Release runs of Sharptillery against the local testapi container, at low priority.

.DESCRIPTION
    Builds the tool in Release and runs a fixed set of short commands against the testapi container
    (http://localhost:8080 by default). For every run it prints the requests, requests per second, errors,
    the tool's CPU time next to the wall time, and its peak working set, as a markdown table.

    With -Baseline <checkout> the same commands also run against a second checkout, built the same way, so
    the table shows before and after on the same PC in the same session.

    Everything runs at BelowNormal priority so a benchmark never makes the PC unresponsive. The testapi
    container is only built, started or stopped when -StartApi / -StopApi say so.

.PARAMETER Repo
    Checkout to build and measure. Default: the parent folder of this script.
.PARAMETER Baseline
    A second checkout to build and measure with the same commands; its rows come first.
.PARAMETER Runs
    Names of the runs to do (default: all): c16, c64, c64-html, slow, flow, c256, rps200, rps100-50ms.
.PARAMETER Target
    Base URL of the testapi. Default http://localhost:8080.
.PARAMETER StartApi
    Build the testapi image from <Repo>/src and (re)start the testapi-dev container before the runs.
.PARAMETER ApiCpus
    CPU limit for the container started by -StartApi (docker --cpus). Default 0: no limit, so the tool
    and not the API is the bottleneck. The VS Code task uses 0.5 to play a small server; that caps the
    API near 5k req/s and makes the throughput rows meaningless.
.PARAMETER StopApi
    Stop and remove the testapi-dev container after the runs.
.PARAMETER SkipBuild
    Use the Release builds that are already there.
.PARAMETER OutFile
    Also write the table to this file.

.EXAMPLE
    pwsh -NoProfile -File tools/bench.ps1 -StartApi -StopApi

.EXAMPLE
    pwsh -NoProfile -File tools/bench.ps1 -Baseline C:\src\sharptillery-main -Runs slow,c64
#>
[CmdletBinding()]
param(
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [string]$Baseline,
    [string[]]$Runs,
    [string]$Target = 'http://localhost:8080',
    [switch]$StartApi,
    [double]$ApiCpus = 0,
    [switch]$StopApi,
    [switch]$SkipBuild,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

# Children inherit the priority class, so the builds and the measured tool all run below normal.
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal

$ContainerName = 'testapi-dev'
$ImageName = 'testapi:dev'

# Peak working set of a process that has already exited: Process.PeakWorkingSet64 refuses once the process is
# gone, but the Win32 counters stay readable through the handle the Process object still holds.
if (-not ('Bench.Native' -as [type])) {
    Add-Type -Namespace Bench -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct PROCESS_MEMORY_COUNTERS
{
    public uint cb;
    public uint PageFaultCount;
    public System.UIntPtr PeakWorkingSetSize;
    public System.UIntPtr WorkingSetSize;
    public System.UIntPtr QuotaPeakPagedPoolUsage;
    public System.UIntPtr QuotaPagedPoolUsage;
    public System.UIntPtr QuotaPeakNonPagedPoolUsage;
    public System.UIntPtr QuotaNonPagedPoolUsage;
    public System.UIntPtr PagefileUsage;
    public System.UIntPtr PeakPagefileUsage;
}

[System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
public static extern bool GetProcessMemoryInfo(System.IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);
'@
}

function Get-PeakWorkingSet([Diagnostics.Process]$Process) {
    $counters = New-Object Bench.Native+PROCESS_MEMORY_COUNTERS
    $counters.cb = [Runtime.InteropServices.Marshal]::SizeOf($counters)
    if ([Bench.Native]::GetProcessMemoryInfo($Process.Handle, [ref]$counters, $counters.cb)) {
        return $counters.PeakWorkingSetSize.ToUInt64()
    }
    return $null
}

function Invoke-Measured([string]$Exe, [string[]]$Arguments, [string]$WorkDir) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $WorkDir

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($psi)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $clock.Stop()
        [pscustomobject]@{
            ExitCode  = $process.ExitCode
            Output    = $stdout.Result
            Error     = $stderr.Result
            Cpu       = $process.TotalProcessorTime
            Wall      = $clock.Elapsed
            PeakBytes = Get-PeakWorkingSet $process
        }
    } finally {
        $process.Dispose()
    }
}

function Get-Number([string]$Text, [string]$Pattern) {
    $m = [regex]::Match($Text, $Pattern)
    if ($m.Success) { return [long]$m.Groups[1].Value }
    return $null
}

# Reads the tool's console output: the flags-mode summary, or the per-phase lines of a YAML run.
function Read-Summary([string]$Output) {
    $requests = Get-Number $Output 'Completed requests:\s+(\d+)'
    $rps = Get-Number $Output 'Average RPS:\s+(\d+)'
    $errors = Get-Number $Output 'Total errors:\s+(\d+)'
    $cancelled = Get-Number $Output 'Cancelled at phase end:\s+(\d+)'

    if ($null -eq $requests) {
        $phases = [regex]::Matches($Output, 'completed: (\d+) requests, (\d+) avg RPS, ([\d.,]+)% errors(?:, (\d+) cancelled at phase end)?')
        if ($phases.Count -gt 0) {
            $requests = 0; $rps = 0; $errors = 0; $cancelled = 0
            foreach ($phase in $phases) {
                $n = [long]$phase.Groups[1].Value
                $requests += $n
                $rps += [long]$phase.Groups[2].Value
                $errors += [long][math]::Round($n * [double]::Parse($phase.Groups[3].Value.Replace(',', '.'), [cultureinfo]::InvariantCulture) / 100)
                if ($phase.Groups[4].Success) { $cancelled += [long]$phase.Groups[4].Value }
            }
        }
    }

    [pscustomobject]@{ Requests = $requests; Rps = $rps; Errors = $errors; Cancelled = $cancelled }
}

function Build-Tool([string]$Checkout) {
    $project = Join-Path $Checkout 'src/SharpArtillery/SharpArtillery.csproj'
    if (-not (Test-Path $project)) { throw "no tool project at $project" }
    if (-not $SkipBuild) {
        Write-Host "bench: building $Checkout (Release)"
        & dotnet build $project -c Release -nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "build failed for $Checkout" }
    }
    $dll = Join-Path $Checkout 'src/SharpArtillery/bin/Release/net10.0/SharpArtillery.dll'
    if (-not (Test-Path $dll)) { throw "no build output at $dll" }
    return $dll
}

function Get-CheckoutLabel([string]$Checkout) {
    # A plain folder (an exported tree) inside another repo would otherwise get that repo's branch.
    $top = & git -C $Checkout rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $top -and ((Resolve-Path $top).Path -eq $Checkout)) {
        $branch = & git -C $Checkout rev-parse --abbrev-ref HEAD 2>$null
        $sha = & git -C $Checkout rev-parse --short HEAD 2>$null
        if ($sha) { return "$branch $sha" }
    }
    return Split-Path $Checkout -Leaf
}

$Invariant = [cultureinfo]::InvariantCulture

function Format-Bytes([nullable[uint64]]$Bytes) {
    if ($null -eq $Bytes) { return 'n/a' }
    return ($Bytes / 1MB).ToString('N0', $Invariant) + ' MB'
}

function Format-Count($Value) {
    if ($null -eq $Value) { return 'n/a' }
    return ([long]$Value).ToString('N0', $Invariant)
}

function Format-Seconds([timespan]$Value) {
    return $Value.TotalSeconds.ToString('0.0', $Invariant) + ' s'
}

$reportDir = Join-Path ([IO.Path]::GetTempPath()) 'sharptillery-bench'
New-Item -ItemType Directory -Force $reportDir | Out-Null

# The card's baseline table, as commands. Durations are short so a full suite stays under two minutes per build.
$Suite = [ordered]@{
    'c16'         = @{ Label = '`-c 16 -d 10s`';                        Args = @('-t', "$Target/Test/get", '-c', '16', '-d', '00:00:10') }
    'c64'         = @{ Label = '`-c 64 -d 10s`';                        Args = @('-t', "$Target/Test/get", '-c', '64', '-d', '00:00:10') }
    'c64-html'    = @{ Label = '`-c 64 -d 10s -o` (HTML)';              Args = @('-t', "$Target/Test/get", '-c', '64', '-d', '00:00:10', '-o', (Join-Path $reportDir 'bench')) }
    'slow'        = @{ Label = '`-c 8 -d 5s`, 500 ms endpoint';         Args = @('-t', "$Target/Test/get?delayMs=500", '-c', '8', '-d', '00:00:05') }
    'flow'        = @{ Label = 'flow YAML (config-flow-test.yaml)';     Args = @('-y', 'src/SharpArtillery/config-flow-test.yaml') }
    'c256'        = @{ Label = '`-c 256 -d 10s`';                       Args = @('-t', "$Target/Test/get", '-c', '256', '-d', '00:00:10') }
    'rps200'      = @{ Label = '`-c 2 -r 200 -d 5s`';                   Args = @('-t', "$Target/Test/get", '-c', '2', '-r', '200', '-d', '00:00:05') }
    'rps100-50ms' = @{ Label = '`-c 10 -r 100 -d 5s`, 50 ms endpoint';  Args = @('-t', "$Target/Test/get?delayMs=50", '-c', '10', '-r', '100', '-d', '00:00:05') }
}

# Through `pwsh -File` the list arrives as one string, so split on commas as well.
$selected = if ($Runs) { @(($Runs -join ',') -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @($Suite.Keys) }
foreach ($name in $selected) {
    if (-not $Suite.Contains($name)) { throw "unknown run '$name'; known: $($Suite.Keys -join ', ')" }
}

$Repo = (Resolve-Path $Repo).Path
if ($Baseline) { $Baseline = (Resolve-Path $Baseline).Path }

try {
    if ($StartApi) {
        Write-Host "bench: building $ImageName and starting $ContainerName"
        & docker build -q -f (Join-Path $Repo 'src/testapi/Dockerfile') -t $ImageName (Join-Path $Repo 'src') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'docker build failed' }
        & docker rm -f $ContainerName 2>$null | Out-Null
        $limits = @('-m', '500m', '--memory-reservation', '50m')
        if ($ApiCpus -gt 0) { $limits += "--cpus=$($ApiCpus.ToString([cultureinfo]::InvariantCulture))" }
        & docker run -d --name $ContainerName -p 8080:80 @limits $ImageName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'docker run failed' }
        $ready = $false
        for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
            Start-Sleep -Milliseconds 500
            try { Invoke-WebRequest "$Target/Test/get" -UseBasicParsing -TimeoutSec 2 | Out-Null; $ready = $true } catch { }
        }
        if (-not $ready) { throw "the API at $Target did not answer within 30 s" }
    }

    $builds = @()
    if ($Baseline) { $builds += [pscustomobject]@{ Label = Get-CheckoutLabel $Baseline; Checkout = $Baseline; Dll = Build-Tool $Baseline } }
    $builds += [pscustomobject]@{ Label = Get-CheckoutLabel $Repo; Checkout = $Repo; Dll = Build-Tool $Repo }

    $rows = @()
    foreach ($name in $selected) {
        $run = $Suite[$name]
        foreach ($build in $builds) {
            Write-Host "bench: $name ($($build.Label))"
            $result = Invoke-Measured 'dotnet' (@($build.Dll) + $run.Args) $build.Checkout
            $summary = Read-Summary $result.Output
            if ($result.ExitCode -ne 0) {
                Write-Host "bench: $name ($($build.Label)) exited with $($result.ExitCode)"
                Write-Host (($result.Error + $result.Output) -split "`n" | Select-Object -Last 10 | Out-String)
            }
            $rows += [pscustomobject]@{
                Run       = $run.Label
                Build     = $build.Label
                Requests  = $summary.Requests
                Rps       = $summary.Rps
                Errors    = $summary.Errors
                Cancelled = $summary.Cancelled
                Cpu       = $result.Cpu
                Wall      = $result.Wall
                PeakBytes = $result.PeakBytes
                ExitCode  = $result.ExitCode
            }
            # Let the API's connections close before the next run.
            Start-Sleep -Seconds 2
        }
    }

    $lines = @('| Run | Build | Requests | req/s | Errors | Cancelled at end | Tool CPU / wall | Peak memory |',
               '|---|---|---|---|---|---|---|---|')
    foreach ($row in $rows) {
        $status = if ($row.ExitCode -ne 0) { " (exit $($row.ExitCode))" } else { '' }
        $lines += ('| {0} | {1}{2} | {3} | {4} | {5} | {6} | {7} / {8} | {9} |' -f
            $row.Run, $row.Build, $status, (Format-Count $row.Requests), (Format-Count $row.Rps), (Format-Count $row.Errors),
            (Format-Count $row.Cancelled), (Format-Seconds $row.Cpu), (Format-Seconds $row.Wall), (Format-Bytes $row.PeakBytes))
    }
    $table = $lines -join [Environment]::NewLine
    Write-Host ''
    Write-Host $table
    if ($OutFile) { [IO.File]::WriteAllText($OutFile, $table + [Environment]::NewLine) }
} finally {
    Remove-Item -LiteralPath $reportDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($StopApi) {
        Write-Host "bench: stopping $ContainerName"
        & docker rm -f $ContainerName 2>$null | Out-Null
    }
}
