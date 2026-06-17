param(
    [string]$PackRoot = "pack",
    [string]$Output = "manifest.json",
    [string]$Name = "Cobblemon Legacy",
    [string]$Version = "1.0.0",
    [string]$MinecraftVersion = "1.21.1",
    [string]$FabricLoaderVersion = "latest",
    [string]$ReleaseBaseUrl = "https://github.com/gotardelo/modpackscobblemonlegacy/releases/download/v1.0.0"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PackRoot)) {
    throw "PackRoot not found: $PackRoot"
}

$root = (Resolve-Path -LiteralPath $PackRoot).Path
$files = @()
$allowedRoots = @("mods", "resourcepacks", "config", "shaderpacks")

foreach ($folder in $allowedRoots) {
    $folderPath = Join-Path $root $folder
    if (-not (Test-Path -LiteralPath $folderPath)) {
        continue
    }

    Get-ChildItem -LiteralPath $folderPath -File -Recurse | Where-Object {
        $_.Name -notin @(".gitkeep", ".DS_Store", "Thumbs.db")
    } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace("\", "/")
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        $escapedPath = ($relative -split "/" | ForEach-Object { [uri]::EscapeDataString($_) }) -join "/"

        $files += [ordered]@{
            path = $relative
            url = "$ReleaseBaseUrl/$escapedPath"
            sha256 = $hash.Hash.ToLowerInvariant()
            size = $_.Length
            required = $true
        }
    }
}

$manifest = [ordered]@{
    name = $Name
    version = $Version
    minecraftVersion = $MinecraftVersion
    fabricLoaderVersion = $FabricLoaderVersion
    files = $files
}

$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Output -Encoding UTF8
Write-Host "Manifest written to $Output with $($files.Count) file(s)."
