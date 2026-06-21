param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SkipDefender,

    [switch]$CreateGitHubRelease
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'CobblemonLegacy.csproj'
$publishDir = Join-Path $root 'publish\win-x64'
$distDir = Join-Path $root 'dist'
$setupPath = Join-Path $distDir 'CobblemonLegacyLauncherSetup.exe'
$releaseTag = "launcher-v$Version"
$releaseDir = Join-Path $distDir $releaseTag

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Comando falhou com exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-InnoCompiler {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw 'Inno Setup 6 nao encontrado. Instale o Inno Setup ou coloque ISCC.exe no PATH.'
}

Invoke-Step 'Conferindo versoes do projeto' {
    $program = Get-Content (Join-Path $root 'Program.cs') -Raw
    $installer = Get-Content (Join-Path $root 'installer\CobblemonLegacy.iss') -Raw
    $csproj = Get-Content $project -Raw

    if ($program -notmatch "LauncherVersion = `"$([regex]::Escape($Version))`"") {
        throw "Program.cs nao esta em $Version."
    }

    if ($installer -notmatch "MyAppVersion `"$([regex]::Escape($Version))`"") {
        throw "CobblemonLegacy.iss nao esta em $Version."
    }

    if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
        throw "CobblemonLegacy.csproj nao esta em $Version."
    }
}

Invoke-Step 'Limpando saidas anteriores' {
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    if (Test-Path $setupPath) {
        Remove-Item $setupPath -Force
    }

    New-Item -ItemType Directory -Force -Path $publishDir, $distDir | Out-Null
}

Invoke-Step 'Restaurando e compilando' {
    Invoke-Native 'dotnet' @('restore', $project, '-r', 'win-x64')
    Invoke-Native 'dotnet' @('build', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore')
}

Invoke-Step 'Publicando executavel' {
    Invoke-Native 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publishDir, '--no-build')
}

Invoke-Step 'Gerando instalador' {
    $iscc = Get-InnoCompiler
    Invoke-Native $iscc @((Join-Path $root 'installer\CobblemonLegacy.iss'))
}

Invoke-Step 'Calculando hashes' {
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    Copy-Item $setupPath (Join-Path $releaseDir 'CobblemonLegacyLauncherSetup.exe') -Force
    Copy-Item (Join-Path $root 'docs\PLAYER_GUIDE.md') $releaseDir -Force
    Copy-Item (Join-Path $root 'docs\QA_CHECKLIST.md') $releaseDir -Force
    Copy-Item (Join-Path $root 'SECURITY_SCAN_REPORT.md') $releaseDir -Force

    $setupHash = Get-FileHash $setupPath -Algorithm SHA256
    $exeHash = Get-FileHash (Join-Path $publishDir 'CobblemonLegacy.exe') -Algorithm SHA256
    $summary = @"
Cobblemon Legacy Launcher $Version

Setup: $($setupHash.Hash)
Exe:   $($exeHash.Hash)

Setup path: $setupPath
Publish:    $publishDir
"@
    $summary | Set-Content (Join-Path $releaseDir 'hashes.txt') -Encoding UTF8
    Write-Host $summary
}

if (-not $SkipDefender) {
    Invoke-Step 'Rodando Microsoft Defender' {
        $mpcmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
        if (-not (Test-Path $mpcmd)) {
            $mpcmd = Join-Path ${env:ProgramFiles(x86)} 'Windows Defender\MpCmdRun.exe'
        }

        if (-not (Test-Path $mpcmd)) {
            Write-Warning 'MpCmdRun.exe nao encontrado. Defender ignorado.'
            return
        }

        Invoke-Native $mpcmd @('-Scan', '-ScanType', '3', '-File', $setupPath)
        Invoke-Native $mpcmd @('-Scan', '-ScanType', '3', '-File', (Join-Path $publishDir 'CobblemonLegacy.exe'))
    }
}

if ($CreateGitHubRelease) {
    Invoke-Step 'Criando release no GitHub' {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if (-not $gh) {
            throw 'GitHub CLI gh nao encontrado.'
        }

        $assets = @(
            (Join-Path $releaseDir 'CobblemonLegacyLauncherSetup.exe'),
            (Join-Path $releaseDir 'PLAYER_GUIDE.md'),
            (Join-Path $releaseDir 'QA_CHECKLIST.md'),
            (Join-Path $releaseDir 'SECURITY_SCAN_REPORT.md'),
            (Join-Path $releaseDir 'hashes.txt')
        )

        gh release create $releaseTag $assets `
            --repo gotardelo/cobblemonlegacy-downloads `
            --title "Cobblemon Legacy Launcher v$Version" `
            --notes "Launcher v${Version}: melhorias de estabilidade, suporte, resourcepacks opcionais e instalador."
    }
}

Write-Host ""
Write-Host "Release local pronta em: $releaseDir" -ForegroundColor Green
