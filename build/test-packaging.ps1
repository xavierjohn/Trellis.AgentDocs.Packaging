[CmdletBinding()]
param([string] $Feed = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\feed'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) "agentdocs-packaging-$([guid]::NewGuid().ToString('N'))"
$publisher = Join-Path $work 'publisher'
$packages = Join-Path $work 'packages'
try {
    New-Item -ItemType Directory -Path $Feed, $publisher, $packages -Force | Out-Null
    & dotnet pack (Join-Path $root 'src\Trellis.AgentDocs.Packaging.csproj') -c Release -o $Feed --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Helper pack failed.' }
    $helper = Join-Path $Feed 'Trellis.AgentDocs.Packaging.0.1.0-preview.1.nupkg'
    $zip = [System.IO.Compression.ZipFile]::OpenRead($helper)
    try {
        if (-not $zip.GetEntry('build/Trellis.AgentDocs.Packaging.targets') -or
            $zip.GetEntry('buildTransitive/Trellis.AgentDocs.Packaging.targets') -or
            @($zip.Entries | Where-Object { $_.FullName -like 'lib/*' }).Count -ne 0) {
            throw 'Helper must have a publisher-only build target and no assembly.'
        }
    }
    finally { $zip.Dispose() }

    $guide = Join-Path $publisher 'overview.md'
    [System.IO.File]::WriteAllText($guide, "# Publisher guide`n", [System.Text.UTF8Encoding]::new($true))
    [System.IO.File]::WriteAllText((Join-Path $publisher 'Publisher.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Independent.Publisher</PackageId>
    <Version>1.0.0</Version>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>NU5128</NoWarn>
    <PackageGuidanceDocument>$(MSBuildProjectDirectory)/overview.md</PackageGuidanceDocument>
    <PackageGuidancePath>guides/overview.md</PackageGuidancePath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Trellis.AgentDocs.Packaging" Version="0.1.0-preview.1" PrivateAssets="all" />
  </ItemGroup>
</Project>
'@)
    $project = Join-Path $publisher 'Publisher.csproj'
    & dotnet restore $project --source $Feed "-p:RestorePackagesPath=$packages" --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Publisher restore failed.' }
    & dotnet pack $project --no-restore -o $Feed --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Publisher pack failed.' }

    $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $Feed 'Independent.Publisher.1.0.0.nupkg'))
    try {
        $manifest = $zip.GetEntry('guidance/reference-manifest.json')
        $document = $zip.GetEntry('guides/overview.md')
        if (-not $manifest -or -not $document) { throw 'Publisher payload is incomplete.' }
        $reader = [System.IO.StreamReader]::new($manifest.Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        $stream = [System.IO.MemoryStream]::new()
        try {
            $document.Open().CopyTo($stream)
            $hash = [Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($stream.ToArray())).ToLowerInvariant()
        }
        finally { $stream.Dispose() }
        if ($metadata.schemaVersion -ne 1 -or @($metadata.documents).Count -ne 1 -or
            $metadata.documents[0].path -ne 'guides/overview.md' -or
            $metadata.documents[0].sha256 -ne $hash -or
            @($metadata.entryPoints).Count -ne 1 -or
            $metadata.entryPoints[0] -ne 'guides/overview.md') {
            throw 'Publisher manifest does not describe the packed guide.'
        }
        if (@($zip.Entries | Where-Object { $_.FullName -match '^(build|buildTransitive)/' }).Count -ne 0) {
            throw 'Publisher leaked a build target.'
        }
        $nuspec = [System.IO.StreamReader]::new($zip.GetEntry('Independent.Publisher.nuspec').Open())
        try {
            if ($nuspec.ReadToEnd().Contains('Trellis.AgentDocs.Packaging', [StringComparison]::Ordinal)) {
                throw 'Publisher leaked the private helper dependency.'
            }
        }
        finally { $nuspec.Dispose() }
    }
    finally { $zip.Dispose() }

    foreach ($invalid in @('../escape.md', 'NUL.md', 'guides/ending./doc.md',
            'guides\backslash.md', 'guides//empty.md')) {
        $output = & dotnet pack $project --no-restore "-p:PackageGuidancePath=$invalid" -o $Feed --nologo -v:q 2>&1
        if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch 'PackageGuidancePath must be a portable relative Markdown path') {
            throw "Unsafe path was not rejected: $invalid : $($output | Out-String)"
        }
    }
    $output = & dotnet pack $project --no-restore "-p:PackageGuidanceDocument=$(Join-Path $publisher 'missing.md')" -o $Feed --nologo -v:q 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch 'Missing package guidance document') {
        throw "Missing guide was not rejected: $($output | Out-String)"
    }
    Write-Host 'PASS independent publisher, package contents, and invalid inputs.'
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
