param(
    [Parameter(Mandatory = $true)][string]$MapPath,
    [Parameter(Mandatory = $true)][string]$GeometryDir,
    [string]$ScriptsDir,
    [string]$TexturesDir,
    [string]$SoundsDir,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$mapFile = (Resolve-Path -LiteralPath $MapPath).Path
$geometryFolder = (Resolve-Path -LiteralPath $GeometryDir).Path
$scriptFolder = if ($ScriptsDir) { (Resolve-Path -LiteralPath $ScriptsDir).Path } else { $null }
$textureFolder = if ($TexturesDir) { (Resolve-Path -LiteralPath $TexturesDir).Path } else { $null }
$soundFolder = if ($SoundsDir) { (Resolve-Path -LiteralPath $SoundsDir).Path } else { $null }
if (Test-Path -LiteralPath $OutputPath) { throw "Output already exists: $OutputPath" }
if (-not $OutputPath.EndsWith('.fihmap.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output file must end in .fihmap.zip.'
}

$map = Get-Content -LiteralPath $mapFile -Raw | ConvertFrom-Json
if (-not $map) { throw 'Invalid map JSON.' }
$geometry = @(Get-ChildItem -LiteralPath $geometryFolder -File -Filter '*.obj')
if ($geometry.Count -eq 0) { throw 'Geometry folder has no .obj files.' }
if ($geometry.Count -gt 100) { throw 'A package can contain at most 100 OBJ files.' }
$scripts = if ($scriptFolder) { @(Get-ChildItem -LiteralPath $scriptFolder -File -Filter '*.cs') } else { @() }
$textures = if ($textureFolder) { @(Get-ChildItem -LiteralPath $textureFolder -File -Filter '*.png') } else { @() }
$sounds = if ($soundFolder) { @(Get-ChildItem -LiteralPath $soundFolder -File | Where-Object Extension -In '.wav', '.ogg') } else { @() }
$names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$scriptNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$textureNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$soundNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $geometry) {
    if ($file.BaseName -cnotmatch '^[A-Za-z_][A-Za-z0-9_]{0,79}$') { throw "Invalid OBJ name: $($file.Name)" }
    if (-not $names.Add($file.BaseName)) { throw "Duplicate OBJ name: $($file.Name)" }
    if ($file.Length -gt 8MB) { throw "OBJ exceeds 8 MB: $($file.Name)" }
}
foreach ($file in $scripts) {
    if (-not $names.Contains($file.BaseName)) { throw "No matching OBJ for script: $($file.Name)" }
    if (-not $scriptNames.Add($file.BaseName)) { throw "Duplicate C# name: $($file.Name)" }
    if ($file.Length -gt 256KB) { throw "C# file exceeds 256 KB: $($file.Name)" }
}
foreach ($file in $textures) {
    if (-not $names.Contains($file.BaseName)) { throw "No matching OBJ for texture: $($file.Name)" }
    if (-not $textureNames.Add($file.BaseName)) { throw "Duplicate PNG name: $($file.Name)" }
    if ($file.Length -gt 8MB) { throw "PNG exceeds 8 MB: $($file.Name)" }
    $png = [System.IO.File]::ReadAllBytes($file.FullName)
    $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    if ($png.Length -lt 33) { throw "Invalid PNG texture: $($file.Name)" }
    $signatureOk = $true
    for ($i = 0; $i -lt 8; $i++) { if ($png[$i] -ne $signature[$i]) { $signatureOk = $false; break } }
    if (-not $signatureOk -or [System.Text.Encoding]::ASCII.GetString($png, 12, 4) -cne 'IHDR' -or
        $png[24] -ne 8 -or $png[25] -notin @(2, 6) -or $png[28] -ne 0) {
        throw "Texture must be a non-interlaced 8-bit RGB/RGBA PNG: $($file.Name)"
    }
    $width = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 16))
    $height = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 20))
    if ($width -lt 1 -or $height -lt 1 -or $width -gt 2048 -or $height -gt 2048) {
        throw "PNG dimensions must be 1–2048 pixels: $($file.Name)"
    }
}
foreach ($file in $sounds) {
    if (-not $names.Contains($file.BaseName)) { throw "No matching OBJ for impact sound: $($file.Name)" }
    if (-not $soundNames.Add($file.BaseName)) { throw "Duplicate impact sound: $($file.BaseName)" }
    if ($file.Length -lt 12 -or $file.Length -gt 4MB) { throw "Impact sound must be 12 bytes to 4 MB: $($file.Name)" }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $head = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
    $wav = $file.Extension -eq '.wav' -and $head -eq 'RIFF' -and [System.Text.Encoding]::ASCII.GetString($bytes, 8, 4) -eq 'WAVE'
    $ogg = $file.Extension -eq '.ogg' -and $head -eq 'OggS'
    if (-not ($wav -or $ogg)) { throw "Impact sound must be WAV or Ogg: $($file.Name)" }
}
$totalBytes = (Get-Item -LiteralPath $mapFile).Length
foreach ($file in $geometry) { $totalBytes += $file.Length }
foreach ($file in $scripts) { $totalBytes += $file.Length }
foreach ($file in $textures) { $totalBytes += $file.Length }
foreach ($file in $sounds) { $totalBytes += $file.Length }
if ($totalBytes -gt 64MB) { throw 'Package contents exceed 64 MB.' }
foreach ($obj in @($map.Objects)) {
    if ($obj.Source -like 'package://*') {
        $name = $obj.Source.Substring('package://'.Length)
        if (-not $names.Contains($name)) { throw "Map references missing OBJ: $name" }
    }
}

$outputFile = [System.IO.Path]::GetFullPath($OutputPath)
$parent = [System.IO.Path]::GetDirectoryName($outputFile)
[System.IO.Directory]::CreateDirectory($parent) | Out-Null
$zip = [System.IO.Compression.ZipFile]::Open($outputFile, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $mapFile, 'map.fihmap.json') | Out-Null
    foreach ($file in $geometry) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "geometry/$($file.BaseName).obj") | Out-Null
    }
    foreach ($file in $scripts) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "scripts/$($file.BaseName).cs") | Out-Null
    }
    foreach ($file in $textures) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "textures/$($file.BaseName).png") | Out-Null
    }
    foreach ($file in $sounds) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "sounds/$($file.Name)") | Out-Null
    }
}
catch {
    $zip.Dispose()
    if (Test-Path -LiteralPath $outputFile) { Remove-Item -LiteralPath $outputFile -Force }
    throw
}
finally { $zip.Dispose() }
Write-Host "Created $outputFile"
