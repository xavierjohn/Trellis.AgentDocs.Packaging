[CmdletBinding()]
param([string] $Feed = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\feed'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'package-version.ps1')
$work = Join-Path ([System.IO.Path]::GetTempPath()) "agentdocs-packaging-$([guid]::NewGuid().ToString('N'))"
$publisher = Join-Path $work 'publisher'
$packages = Join-Path $work 'packages'
try {
    New-Item -ItemType Directory -Path $Feed, $publisher, $packages -Force | Out-Null
    & dotnet pack (Join-Path $root 'src\Trellis.AgentDocs.Packaging.csproj') -c Release -o $Feed --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Helper pack failed.' }
    $version = Get-NuGetPackageVersion (Join-Path $root 'src\Trellis.AgentDocs.Packaging.csproj')
    $helper = Join-Path $Feed "Trellis.AgentDocs.Packaging.$version.nupkg"
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
    [System.IO.File]::WriteAllText((Join-Path $publisher 'Publisher.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Independent.Publisher</PackageId>
    <Version>1.0.0</Version>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>NU5128</NoWarn>
    <PackageGuidanceDocument>`$(MSBuildProjectDirectory)/overview.md</PackageGuidanceDocument>
    <PackageGuidancePath>guides/overview.md</PackageGuidancePath>
    <PackageGuidanceUsage>required</PackageGuidanceUsage>
    <PackageGuidanceDescription>Open for "Independent" APIs; it's C:\docs &amp; 100% of cases.</PackageGuidanceDescription>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Trellis.AgentDocs.Packaging" Version="$version" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@)
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
            $metadata.documents[0].usage -ne 'required' -or
            $metadata.documents[0].description -cne 'Open for "Independent" APIs; it''s C:\docs & 100% of cases.' -or
            $null -ne $metadata.PSObject.Properties['entryPoints']) {
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
    # Length is counted in Unicode scalar values after NFC, exactly as the reader counts it.
    $validDescriptions = [ordered]@{
        '200 emoji (400 UTF-16 units)' = ([char]::ConvertFromUtf32(0x1F600) * 200)
        '101 decomposed letters (202 UTF-16 units, 101 after NFC)' = (('e' + [char]0x0301) * 101)
        "an apostrophe like it's here" = "an apostrophe like it's here"
    }
    foreach ($name in $validDescriptions.Keys) {
        $output = & dotnet pack $project --no-restore "-p:PackageGuidanceDescription=$($validDescriptions[$name])" -o $Feed --nologo -v:q 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Valid description was rejected: $name : $($output | Out-String)"
        }
    }
    $invalidInputs = [ordered]@{
        'PackageGuidanceUsage=supporting' = 'PackageGuidanceUsage must be required or onDemand'
        'PackageGuidanceUsage=always' = 'PackageGuidanceUsage must be required or onDemand'
        'PackageGuidanceDescription=' = 'Set PackageGuidanceDescription'
        "PackageGuidanceDescription=$('x' * 201)" = 'at most 200 characters'
        "PackageGuidanceDescription=zero$([char]0x200B)width" = 'at most 200 characters'
        "PackageGuidanceDescription=line$([char]0x2028)break" = 'at most 200 characters'
        "PackageGuidanceDescription=$([char]0x00A0)" = 'at most 200 characters'
        "PackageGuidanceDescription=$([char]::ConvertFromUtf32(0x1F600) * 201)" = 'at most 200 characters'
    }
    foreach ($property in $invalidInputs.Keys) {
        $output = & dotnet pack $project --no-restore "-p:$property" -o $Feed --nologo -v:q 2>&1
        if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch $invalidInputs[$property]) {
            throw "Invalid guidance property was not rejected: $property : $($output | Out-String)"
        }
    }
    $output = & dotnet pack $project --no-restore "-p:PackageGuidanceDocument=$(Join-Path $publisher 'missing.md')" -o $Feed --nologo -v:q 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch 'Missing package guidance document') {
        throw "Missing guide was not rejected: $($output | Out-String)"
    }
    Write-Host 'PASS independent publisher, package contents, and invalid inputs.'

    # Multi-document publisher: PackageGuidanceItem items with mixed usage, checked by the shipped validator.
    $multi = Join-Path $work 'multi'
    New-Item -ItemType Directory -Path $multi | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $multi 'start.md'), "# Start`n`nSee [http](http.md#calling-x).`n")
    [System.IO.File]::WriteAllText((Join-Path $multi 'http.md'), "# Http`n`n## Calling X`n")
    [System.IO.File]::WriteAllText((Join-Path $multi 'cookbook.md'), "# Cookbook`n")
    $validItems = @'
    <PackageGuidanceItem Include="start.md" PackagePath="guide/start.md" Usage="required"
                         Description="Read before using X; it's &amp; 100% needed." />
    <PackageGuidanceItem Include="cookbook.md" PackagePath="guide/cookbook.md"
                         Description="Open when writing recipes." />
    <PackageGuidanceItem Include="http.md" PackagePath="guide/http.md" Usage="supporting" />
'@
    function New-MultiProject([string] $Name, [string] $Items, [string] $Extra = '') {
        $dir = Join-Path $multi $Name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        foreach ($file in 'start.md', 'cookbook.md', 'http.md') { Copy-Item (Join-Path $multi $file) $dir }
        [System.IO.File]::WriteAllText((Join-Path $dir "$Name.csproj"), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Multi.$Name</PackageId>
    <Version>1.0.0</Version>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>NU5128</NoWarn>
    $Extra
  </PropertyGroup>
  <ItemGroup>
$Items
    <PackageReference Include="Trellis.AgentDocs.Packaging" Version="$version" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@)
        & dotnet restore (Join-Path $dir "$Name.csproj") --source $Feed "-p:RestorePackagesPath=$packages" --nologo -v:q | Out-Null
        return (Join-Path $dir "$Name.csproj")
    }

    $good = New-MultiProject 'Good' $validItems
    & dotnet pack $good --no-restore -o $Feed --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Multi-document publisher pack failed.' }
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $Feed 'Multi.Good.1.0.0.nupkg'))
    try {
        $reader = [System.IO.StreamReader]::new($zip.GetEntry('guidance/reference-manifest.json').Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $docs = @($metadata.documents)
        if ($docs.Count -ne 3 -or
            @($docs.path) -join ',' -ne 'guide/start.md,guide/cookbook.md,guide/http.md' -or
            @($docs.usage) -join ',' -ne 'required,onDemand,supporting' -or
            $docs[0].description -cne 'Read before using X; it''s & 100% needed.' -or
            $docs[2].PSObject.Properties['description'] -or
            $null -ne $metadata.PSObject.Properties['entryPoints']) {
            throw "Multi-document manifest is wrong: $($docs | ConvertTo-Json -Compress)"
        }
        foreach ($doc in $docs) {
            if (-not $zip.GetEntry($doc.path)) { throw "Multi-document package is missing $($doc.path)" }
        }
    }
    finally { $zip.Dispose() }
    # Round trip: whatever the helper packs must pass the validator that consumers' authors run.
    $validation = & dotnet run --project (Join-Path $root 'Trellis.AgentDocs\src\Trellis.AgentDocs.csproj') -c Release `
        -- validate (Join-Path $Feed 'Multi.Good.1.0.0.nupkg') --strict 2>&1
    if ($LASTEXITCODE -ne 0) { throw "agentdocs validate rejected the helper's output: $($validation | Out-String)" }

    $multiInvalid = [ordered]@{
        'Duplicate' = @{ Items = ($validItems + '<PackageGuidanceItem Include="http.md" PackagePath="guide/start.md" Usage="supporting" />'); Match = 'listed more than once' }
        'NoDescription' = @{ Items = '<PackageGuidanceItem Include="start.md" PackagePath="guide/start.md" Usage="required" />'; Match = 'not blank' }
        'OnlySupporting' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="guide/http.md" Usage="supporting" />'; Match = 'at least one required or onDemand' }
        'BadUsage' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="guide/http.md" Usage="always" Description="x" />'; Match = 'Usage must be required, onDemand or supporting' }
        'BadPath' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="../http.md" Description="x" />'; Match = 'portable relative Markdown path' }
        'Missing' = @{ Items = '<PackageGuidanceItem Include="nope.md" PackagePath="guide/nope.md" Description="x" />'; Match = 'missing guidance document' }
        'LongSupportingDescription' = @{ Items = ($validItems + '<PackageGuidanceItem Include="http.md" PackagePath="guide/other.md" Usage="supporting" Description="' + ('x' * 201) + '" />'); Match = 'at most 200 characters' }
        'BothForms' = @{ Items = $validItems; Extra = '<PackageGuidancePath>guide/x.md</PackageGuidancePath>'; Match = 'not both' }
    }
    foreach ($case in $multiInvalid.Keys) {
        $extra = if ($multiInvalid[$case].ContainsKey('Extra')) { $multiInvalid[$case].Extra } else { '' }
        $bad = New-MultiProject "Bad$case" $multiInvalid[$case].Items $extra
        $output = & dotnet pack $bad --no-restore -o $Feed --nologo -v:q 2>&1
        if ($LASTEXITCODE -eq 0 -or ($output | Out-String) -notmatch $multiInvalid[$case].Match) {
            throw "Invalid multi-document input was not rejected: $case : $($output | Out-String)"
        }
    }
    Write-Host 'PASS multi-document publisher, validator round trip, and invalid inputs.'
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
exit 0
