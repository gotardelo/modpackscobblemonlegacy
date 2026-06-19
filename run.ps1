param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$LauncherArgs
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'CobblemonLegacy.csproj'
$intermediateDir = Join-Path $PSScriptRoot 'obj\Debug\net10.0-windows'
$requiredGeneratedFiles = @(
    'App.g.cs',
    'LoginChoiceWindow.g.cs',
    'MainWindow.g.cs',
    'OptionsWindow.g.cs'
)

function Get-DotnetPath {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $defaultPath = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path $defaultPath) {
        return $defaultPath
    }

    throw 'Nao encontrei o dotnet instalado no PATH.'
}

function Repair-WpfIntermediateFilesIfNeeded {
    if (-not (Test-Path $intermediateDir)) {
        return
    }

    foreach ($file in $requiredGeneratedFiles) {
        if (-not (Test-Path (Join-Path $intermediateDir $file))) {
            Write-Host 'Cache de build WPF incompleto. Limpando obj/Debug...'
            Remove-Item $intermediateDir -Recurse -Force
            return
        }
    }
}

$dotnet = Get-DotnetPath
Repair-WpfIntermediateFilesIfNeeded

Write-Host "Abrindo Cobblemon Legacy Launcher..."
& $dotnet run --project $projectPath -- @LauncherArgs
exit $LASTEXITCODE
