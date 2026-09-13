<#
    build.ps1 - offline build script for "LastRegret" (project-019)
    Project folder: projects\project-019-zuihouhui-de-ctrlz
    -----------------------------------------------------------------
    NOTE 1 - this file is intentionally PURE ASCII (English comments and
      messages). Windows PowerShell 5.1 reads a .ps1 without a UTF-8 BOM
      as ANSI/GBK, which turns Chinese text into mojibake and breaks the
      parser. Keeping the script ASCII removes that whole class of error.

    NOTE 2 - the project folder uses pinyin (zuihouhui-de-ctrlz) instead
      of Chinese, because Chinese paths plus special characters break
      argument parsing / encoding conversion in some build tools.

    Machine constraints (measured 2026-09-11):
      1. No system-wide .NET SDK. Only a portable SDK 8.0.424 that ships
         inside another project (its folder name contains Chinese, but it
         is only used as a FILE PATH, never as a parsed argument).
      2. NuGet is completely unreachable (direct and via proxy 7890).
      3. That SDK bundles Microsoft.NETCore.App.Ref and
         Microsoft.WindowsDesktop.App.Ref, so WPF + net8.0-windows builds
         fully offline (zero NuGet packages).
      4. The portable SDK silently fails restore evaluation for referenced
         projects when MSBuild runs in PARALLEL, and swallows the real
         compiler errors. Serial builds (-m:1) are mandatory here.

    Usage (Windows PowerShell 5.1 or PowerShell 7+):
      powershell -ExecutionPolicy Bypass -File build.ps1
      powershell -ExecutionPolicy Bypass -File build.ps1 -Config Release
      powershell -ExecutionPolicy Bypass -File build.ps1 -Target test
      powershell -ExecutionPolicy Bypass -File build.ps1 -Target run
      powershell -ExecutionPolicy Bypass -File build.ps1 -Target clean
      powershell -ExecutionPolicy Bypass -File build.ps1 -Target publish
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$Config = 'Debug',
    [ValidateSet('build','test','run','clean','publish')] [string]$Target = 'build',
    [switch]$SelfContained = $false,
    [string]$DotnetPath = ''
)

$ErrorActionPreference = 'Stop'
# This script lives in <code>\build\, while the solution and the offline cache
# live in <code>\. So the working root is one level up.
$buildDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$here = (Resolve-Path -LiteralPath (Join-Path $buildDir '..')).Path

# ---------------------------------------------------------------------------
# Locate a dotnet that can actually COMPILE (needs an "sdk" folder;
# a runtime-only dotnet cannot build anything).
# ---------------------------------------------------------------------------
function Find-DotnetPath {
    param([string]$Hint)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Hint) { $candidates.Add($Hint) }
    if ($env:LR_DOTNET) { $candidates.Add($env:LR_DOTNET) }

    # 这里**刻意不写任何本机专属路径**。
    #
    # 曾经硬编码过一条开发机上的便携 SDK 路径（形如 D:\<工作区>\projects\<某个
    # 不相关项目>\tools\dotnet\dotnet.exe）。那是开发机的目录结构，对别人毫无用处，
    # 公开出去只会暴露本机环境，所以已删除。
    #
    # 需要指定 SDK 时用下面两种方式之一（都不需要改仓库）：
    #   · 命令行：build.ps1 -DotnetPath "D:\somewhere\dotnet\dotnet.exe"
    #   · 环境变量：set LR_DOTNET=D:\somewhere\dotnet\dotnet.exe
    # 否则按通用顺序查找：ProgramFiles → LOCALAPPDATA → PATH（并校验确实带 sdk 目录）。

    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        if ($c.Contains('*')) {
            $glob = Get-ChildItem -Path $c -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($glob) { $c = $glob.FullName } else { continue }
        }
        if (-not (Test-Path -LiteralPath $c)) { continue }
        $root = Split-Path -Parent $c
        if (Test-Path -LiteralPath (Join-Path $root 'sdk')) { return $c }
    }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($sdks) { return $cmd.Source }
    }

    throw ('No usable .NET SDK found (need a dotnet.exe that has an "sdk" folder). ' +
           'Set LR_DOTNET or pass -DotnetPath.')
}

$dotnet = Find-DotnetPath -Hint $DotnetPath
Write-Host "[build] dotnet  = $dotnet" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# Offline environment (all four are required on this machine)
# ---------------------------------------------------------------------------
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$offline    = Join-Path $here '.offline'
$nugetCache = Join-Path $offline 'nuget-cache'
$cliHome    = Join-Path $offline 'cli-home'
$appDataDir = Join-Path $offline 'appdata'
foreach ($d in @($offline, $nugetCache, $cliHome, $appDataDir)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}

# NuGet package cache must live inside the workspace (the user profile is not writable here).
$env:NUGET_PACKAGES = $nugetCache
# Redirect the CLI home and APPDATA, otherwise dotnet tries to read
#   C:\Users\<user>\AppData\Roaming\NuGet\NuGet.Config
# which is denied in this environment and makes restore fail outright.
$env:DOTNET_CLI_HOME = $cliHome
$env:APPDATA         = $appDataDir

$sln      = Join-Path $here 'LastRegret.sln'
$testProj = Join-Path $here 'tests\LastRegret.Tests\LastRegret.Tests.csproj'

# ---------------------------------------------------------------------------
# Time zone alignment (tests only)
# ---------------------------------------------------------------------------
# A sandboxed terminal may start this script with TZ=UTC while the real system
# time zone is UTC+8. That makes DateTime.Now differ from the system clock by
# 8 hours and breaks every local<->UTC round trip in the restore tests.
# TZ must be set BEFORE the tested process starts (.NET caches the zone on Windows).
function Set-SystemTimeZone {
    $tz = $null
    try {
        $key = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation' -ErrorAction Stop
        if ($key.TimeZoneKeyName) { $tz = $key.TimeZoneKeyName }
    } catch { }
    if ($tz) {
        $env:TZ = $tz
        Write-Host "[build] test process time zone = $tz" -ForegroundColor DarkGray
    }
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ('dotnet ' + ($Arguments -join ' ') + ' failed (exit ' + $LASTEXITCODE + ')')
    }
}

# Serial build arguments - see NOTE 4 at the top of this file.
$msArgs = @('-m:1', '--nologo')

switch ($Target) {
    'clean' {
        Invoke-Dotnet (@('clean', $sln, '-c', $Config) + $msArgs + @('-v', 'q'))
    }
    'build' {
        Invoke-Dotnet (@('build', $sln, '-c', $Config) + $msArgs + @('-v', 'm'))
    }
    'test' {
        Invoke-Dotnet (@('build', $sln, '-c', $Config) + $msArgs + @('-v', 'q'))
        Set-SystemTimeZone
        Invoke-Dotnet @('run', '--project', $testProj, '-c', $Config, '--no-build')
    }
    'run' {
        Invoke-Dotnet (@('build', $sln, '-c', $Config) + $msArgs + @('-v', 'q'))
        $exe = Join-Path $here ('src\LastRegret.App\bin\' + $Config + '\net8.0-windows\win-x64\LastRegret.exe')
        if (-not (Test-Path -LiteralPath $exe)) {
            $exe = Join-Path $here ('src\LastRegret.App\bin\' + $Config + '\net8.0-windows\LastRegret.exe')
        }
        if (-not (Test-Path -LiteralPath $exe)) { throw ('Executable not found: ' + $exe) }
        Write-Host "[build] starting $exe" -ForegroundColor Cyan
        Start-Process -FilePath $exe | Out-Null
    }
    'publish' {
        # -------------------------------------------------------------------
        # Produce a distributable package.
        #
        # Prelaunch-audit blockers 1 and 2:
        #   * Release + DebugType=none  -> no .pdb in the package
        #   * only the publish output goes into the zip (never bin/obj)
        #
        # Self-contained vs framework-dependent:
        #   This project has a hard rule of ZERO NuGet packages, enforced by
        #   code\NuGet.Config which does <clear/> on every package source.
        #   A self-contained publish needs Microsoft.WindowsDesktop.App.Runtime
        #   from NuGet, so it CANNOT work here (fails with NU1100).
        #   Framework-dependent publish needs nothing from NuGet -> default.
        #   It requires .NET 8 Desktop Runtime on the target machine.
        #   -SelfContained builds the bigger bundle (needs a reachable NuGet
        #   source; remove the clear/> for that run).
        # -------------------------------------------------------------------
        $appProj = Join-Path $here 'src\LastRegret.App\LastRegret.App.csproj'
        $stage   = Join-Path $here '..\dist'
        $pubDir  = Join-Path $stage 'TimeBack'
        $zipPath = Join-Path $stage 'TimeBack-win-x64.zip'

        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

        $scValue = 'false'
        if ($SelfContained) { $scValue = 'true' }

        Invoke-Dotnet (@('publish', $appProj, '-c', 'Release', '-r', 'win-x64',
                         '--self-contained', $scValue,
                         '-p:DebugType=none', '-p:DebugSymbols=false',
                         '-o', $pubDir) + $msArgs + @('-v', 'q'))

        # Safety net: blockers must stay fixed even if a property is ignored.
        $strays = Get-ChildItem -LiteralPath $pubDir -Recurse -File |
                  Where-Object { $_.Extension -in @('.pdb', '.ilk', '.exp', '.lib') }
        if ($strays) {
            Write-Host '[build] removing debug/symbol leftovers:' -ForegroundColor Yellow
            foreach ($x in $strays) { Write-Host ('  - ' + $x.Name); Remove-Item -LiteralPath $x.FullName -Force }
        }

        Compress-Archive -Path (Join-Path $pubDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $zipName = Split-Path $zipPath -Leaf
        $size = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
        $fileCount = (Get-ChildItem -LiteralPath $pubDir -Recurse -File).Count
        $shaLine = $hash + '  ' + $zipName
        Set-Content -LiteralPath (Join-Path $stage 'SHA256.txt') -Encoding ASCII -Value $shaLine

        $note = 'framework-dependent (needs .NET 8 Desktop Runtime)'
        if ($SelfContained) { $note = 'self-contained (no runtime needed)' }

        Write-Host ''
        Write-Host '[build] package ready' -ForegroundColor Green
        Write-Host ('  zip      : ' + $zipPath)
        Write-Host ('  size     : ' + $size + ' MB   (' + $fileCount + ' files)')
        Write-Host ('  mode     : ' + $note)
        Write-Host ('  sha256   : ' + $hash)
    }
}

Write-Host "[build] done: target=$Target config=$Config" -ForegroundColor Green
