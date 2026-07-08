param(
    [string]$PackRoot = "pack",
    [string]$Manifest = "manifest.json"
)

$ErrorActionPreference = "Stop"

function Add-Issue {
    param(
        [string]$Kind,
        [string]$Message
    )

    [pscustomobject]@{
        Kind = $Kind
        Message = $Message
    }
}

if (-not (Test-Path -LiteralPath $PackRoot)) {
    throw "PackRoot not found: $PackRoot"
}

$issues = @()
$root = (Resolve-Path -LiteralPath $PackRoot).Path
$rootWithSeparator = $root.TrimEnd("\", "/") + [IO.Path]::DirectorySeparatorChar
$managedRoots = @("mods", "resourcepacks", "config", "shaderpacks", "datapacks")

$packFiles = foreach ($folder in $managedRoots) {
    $folderPath = Join-Path $root $folder
    if (Test-Path -LiteralPath $folderPath) {
        Get-ChildItem -LiteralPath $folderPath -File -Recurse | Where-Object {
            $_.Name -notin @(".gitkeep", ".DS_Store", "Thumbs.db")
        } | ForEach-Object {
            [pscustomobject]@{
                Root = $folder
                RelativePath = $_.FullName.Substring($rootWithSeparator.Length).Replace("\", "/")
                Name = $_.Name
                FullName = $_.FullName
            }
        }
    }
}

$duplicatePaths = @($packFiles | Group-Object RelativePath | Where-Object Count -gt 1)
foreach ($group in $duplicatePaths) {
    $issues += Add-Issue "duplicate-path" "Duplicated pack path: $($group.Name)"
}

$duplicateManagedNames = @(
    $packFiles |
        Where-Object { $_.Root -in @("mods", "resourcepacks", "datapacks") } |
        Group-Object Root, Name |
        Where-Object Count -gt 1
)
foreach ($group in $duplicateManagedNames) {
    $issues += Add-Issue "duplicate-name" "Duplicated file name in $($group.Group[0].Root): $($group.Group[0].Name)"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$modRows = foreach ($mod in @($packFiles | Where-Object { $_.Root -eq "mods" -and $_.Name.EndsWith(".jar", [StringComparison]::OrdinalIgnoreCase) })) {
    $zip = $null
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($mod.FullName)
        $entry = $zip.Entries | Where-Object { $_.FullName -eq "fabric.mod.json" } | Select-Object -First 1
        if (-not $entry) {
            [pscustomobject]@{ Id = ""; File = $mod.RelativePath }
            continue
        }

        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            $metadata = $reader.ReadToEnd() | ConvertFrom-Json
            [pscustomobject]@{ Id = [string]$metadata.id; File = $mod.RelativePath }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        if ($zip) {
            $zip.Dispose()
        }
    }
}

foreach ($row in @($modRows | Where-Object { [string]::IsNullOrWhiteSpace($_.Id) })) {
    $issues += Add-Issue "missing-fabric-metadata" "Mod jar has no fabric.mod.json: $($row.File)"
}

$duplicateModIds = @($modRows | Where-Object Id | Group-Object Id | Where-Object Count -gt 1)
foreach ($group in $duplicateModIds) {
    $files = ($group.Group.File -join ", ")
    $issues += Add-Issue "duplicate-mod-id" "Duplicated Fabric mod id '$($group.Name)': $files"
}

if (Test-Path -LiteralPath $Manifest) {
    $manifestData = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $manifestPaths = @($manifestData.files | ForEach-Object { [string]$_.path })

    foreach ($group in @($manifestPaths | Group-Object | Where-Object Count -gt 1)) {
        $issues += Add-Issue "duplicate-manifest-path" "Duplicated manifest path: $($group.Name)"
    }

    foreach ($group in @($manifestPaths | ForEach-Object { [IO.Path]::GetFileName($_) } | Group-Object | Where-Object Count -gt 1)) {
        $issues += Add-Issue "duplicate-manifest-name" "Duplicated manifest file name: $($group.Name)"
    }

    $assetNames = @($manifestData.files | ForEach-Object {
        [Uri]::UnescapeDataString(([Uri]$_.url).Segments[-1])
    })
    foreach ($group in @($assetNames | Group-Object | Where-Object Count -gt 1)) {
        $issues += Add-Issue "duplicate-asset-name" "Duplicated release asset name in manifest URLs: $($group.Name)"
    }
}

$summary = [pscustomobject]@{
    PackRoot = $root
    Files = @($packFiles).Count
    Mods = @($packFiles | Where-Object Root -eq "mods").Count
    Resourcepacks = @($packFiles | Where-Object Root -eq "resourcepacks").Count
    Datapacks = @($packFiles | Where-Object Root -eq "datapacks").Count
    Issues = @($issues).Count
}

$summary | Format-List

if ($issues.Count -gt 0) {
    $issues | Format-Table -AutoSize
    throw "Pack integrity check failed with $($issues.Count) issue(s)."
}

Write-Host "Pack integrity check passed: no duplicated mods, resourcepacks or datapacks."
