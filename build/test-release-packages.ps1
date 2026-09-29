[CmdletBinding()]
param(
    [string] $PackagesDirectory = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\release'),
    [switch] $CheckSourceVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'package-version.ps1')

$ids = @('Trellis.AgentDocs.Packaging', 'Trellis.AgentDocs')
$packages = @(Get-ChildItem -LiteralPath $PackagesDirectory -Filter '*.nupkg' -File)
if ($packages.Count -ne $ids.Count) {
    throw "Expected exactly two release packages in $PackagesDirectory, found $($packages.Count)."
}

$versions = @()
foreach ($id in $ids) {
    $matching = @($packages | Where-Object {
        $_.Name -match "^$([regex]::Escape($id))\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\.nupkg$"
    })
    if ($matching.Count -ne 1) { throw "Expected exactly one $id package." }
    $package = $matching[0]
    $version = $package.Name.Substring($id.Length + 1, $package.Name.Length - $id.Length - '.nupkg'.Length - 1)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entry = $archive.GetEntry("$id.nuspec")
        if (-not $entry) { throw "Missing nuspec in $($package.Name)." }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { [xml] $nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if (-not $metadata -or $metadata.SelectSingleNode('*[local-name()="id"]').InnerText -ne $id -or
            $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -ne $version) {
            throw "Nuspec identity/version does not match $($package.Name)."
        }
        if ($metadata.SelectSingleNode('*[local-name()="icon"]')?.InnerText -ne 'icon.png') {
            throw "$id must declare the Trellis package icon."
        }
        $icon = $archive.GetEntry('icon.png')
        if (-not $icon) { throw "$id must pack icon.png at the package root." }
        $iconBytes = [System.IO.MemoryStream]::new()
        $iconStream = $icon.Open()
        try {
            $iconStream.CopyTo($iconBytes)
            $packedHash = [Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($iconBytes.ToArray()))
        }
        finally {
            $iconStream.Dispose()
            $iconBytes.Dispose()
        }
        if ($packedHash -ne (Get-FileHash (Join-Path $root 'icon.png') -Algorithm SHA256).Hash) {
            throw "$id packed an unexpected package icon."
        }
    }
    finally { $archive.Dispose() }
    $versions += $version
}

if ($versions[0] -ne $versions[1]) {
    throw "Release package versions do not match: $($versions -join ', ')."
}
if ($CheckSourceVersion) {
    foreach ($project in @('src\Trellis.AgentDocs.Packaging.csproj',
            'Trellis.AgentDocs\src\Trellis.AgentDocs.csproj')) {
        $computed = Get-NuGetPackageVersion (Join-Path $root $project)
        if ($computed -ne $versions[0]) {
            throw "NBGV version $computed does not match packed version $($versions[0])."
        }
    }
}
Write-Host "PASS both release packages share version $($versions[0]) and have matching nuspecs."
