param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$LauncherArgs
)

Write-Host "Abrindo Cobblemon Legacy Launcher..."
& 'C:\Program Files\dotnet\dotnet.exe' run --project "$PSScriptRoot\CobblemonLegacy.csproj" -- @LauncherArgs
exit $LASTEXITCODE
