[CmdletBinding()]
param([string] $Feed = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\feed'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'package-version.ps1')
$work = Join-Path ([System.IO.Path]::GetTempPath()) "agentdocs-packaging-$([guid]::NewGuid().ToString('N'))"
$originalNuGetPackages = $env:NUGET_PACKAGES
$originalDotNetCliHome = $env:DOTNET_CLI_HOME
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

    foreach ($invalid in @('../escape.md', '.github/guide.md', 'NUL.md', 'guides/ending./doc.md',
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
    [System.IO.File]::WriteAllText((Join-Path $multi 'start.md'),
        "# Start`n`nSee [http](http.md#calling-x) and [publisher](publisher.md#publisher-guide).`n")
    [System.IO.File]::WriteAllText((Join-Path $multi 'http.md'), "# Http`n`n## Calling X`n")
    [System.IO.File]::WriteAllText((Join-Path $multi 'cookbook.md'), "# Cookbook`n")
    $validItems = @'
    <PackageGuidanceItem Include="start.md" PackagePath="guide/start.md" Usage="required"
                         Description="Read before using X; it's &amp; 100% needed." />
    <PackageGuidanceItem Include="cookbook.md" PackagePath="guide/cookbook.md"
                         Description="Open when writing recipes." />
    <PackageGuidanceItem Include="http.md" PackagePath="guide/http.md" Usage="supporting" />
    <PackageGuidanceReference Include="guide/publisher.md" PackageId="Independent.Publisher"
                              DocumentPath="guides/overview.md" />
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
    <PackageReference Include="Independent.Publisher" Version="1.0.0" />
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
        if ($metadata.schemaVersion -ne 1 -or $docs.Count -ne 3 -or
            @($docs.path) -join ',' -ne 'guide/start.md,guide/cookbook.md,guide/http.md' -or
            @($docs.usage) -join ',' -ne 'required,onDemand,supporting' -or
            $docs[0].description -cne 'Read before using X; it''s & 100% needed.' -or
            $docs[2].PSObject.Properties['description'] -or
            @($metadata.documentReferences).Count -ne 1 -or
            $metadata.documentReferences[0].path -cne 'guide/publisher.md' -or
            $metadata.documentReferences[0].packageId -cne 'Independent.Publisher' -or
            $metadata.documentReferences[0].documentPath -cne 'guides/overview.md' -or
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

    # Conformance: a consumer's own tool must accept and install what the helper produced, not only the validator.
    $consumer = Join-Path $work 'consumer'
    New-Item -ItemType Directory -Path $consumer | Out-Null
    & git init -q $consumer
    [System.IO.File]::WriteAllText((Join-Path $consumer 'Consumer.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Multi.Good" Version="1.0.0" /></ItemGroup>
</Project>
"@)
    [System.IO.File]::WriteAllText((Join-Path $consumer 'NuGet.Config'),
        "<configuration><packageSources><clear/><add key=`"feed`" value=`"$Feed`"/></packageSources></configuration>")
    & dotnet restore (Join-Path $consumer 'Consumer.csproj') "-p:RestorePackagesPath=$(Join-Path $work 'consumer-packages')" --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Consumer restore of the multi-document package failed.' }
    # The tool insists on being pinned at the consumer's Git root, so install the freshly packed one as a consumer would.
    $toolProject = Join-Path $root 'Trellis.AgentDocs\src\Trellis.AgentDocs.csproj'
    & dotnet pack $toolProject -c Release -o $Feed --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Tool pack failed.' }
    $toolVersion = Get-NuGetPackageVersion $toolProject
    $env:NUGET_PACKAGES = Join-Path $work 'tool-packages'
    $env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    Push-Location $consumer
    try {
        & dotnet new tool-manifest --output .config | Out-Null
        & dotnet tool install Trellis.AgentDocs --version $toolVersion --add-source $Feed --tool-manifest .config/dotnet-tools.json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Installing the packed tool failed.' }
        $init = & dotnet tool run agentdocs init Consumer.csproj 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Consumer init failed for the multi-document package: $($init | Out-String)" }
        [System.IO.File]::WriteAllText((Join-Path $consumer '.agentdocs\policy.json'),
            '{ "schemaVersion": 1, "approvedPackages": ["Independent.Publisher", "Multi.Good"] }')
        $sync = & dotnet tool run agentdocs sync 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Consumer sync rejected the helper's output: $($sync | Out-String)" }
        $check = & dotnet tool run agentdocs check --strict-references 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Consumer strict reference check failed: $($check | Out-String)" }
    }
    finally { Pop-Location }
    foreach ($installed in 'start.md', 'cookbook.md', 'http.md') {
        if (-not (Test-Path (Join-Path $consumer ".agentdocs\packages\multi.good\guide\$installed"))) {
            throw "Consumer did not install $installed from the multi-document package."
        }
    }
    if (-not (Test-Path (Join-Path $consumer '.agentdocs\packages\independent.publisher\guides\overview.md'))) {
        throw 'Consumer did not install the approved cross-package reference target.'
    }
    $rewrittenStart = Get-Content -LiteralPath (
        Join-Path $consumer '.agentdocs\packages\multi.good\guide\start.md') -Raw
    if ($rewrittenStart -notmatch '\.\./\.\./independent\.publisher/guides/overview\.md#publisher-guide' -or
        $rewrittenStart -match '\]\(publisher\.md#publisher-guide\)') {
        throw "Consumer did not rewrite the cross-package document link: $rewrittenStart"
    }
    $state = Get-Content -LiteralPath (Join-Path $consumer '.agentdocs\agent-context.json') -Raw | ConvertFrom-Json
    $sourceState = @($state.References | Where-Object Path -eq 'packages/multi.good/guide/start.md')
    if ($state.SchemaVersion -ne 3 -or $sourceState.Count -ne 1 -or
        $sourceState[0].TransformVersion -ne 'document-references-v1' -or
        @($sourceState[0].ReferenceDependencies).Count -ne 1 -or
        $sourceState[0].ReferenceDependencies[0].Outcome -ne 'resolved' -or
        $sourceState[0].ReferenceDependencies[0].TargetVersion -ne '1.0.0' -or
        $sourceState[0].ReferenceDependencies[0].TargetSha256.Length -ne 64) {
        throw 'Consumer context does not record the resolved reference dependency.'
    }
    # Three-way conformance: the raw nupkg, the directory NuGet extracted from it, and what the consumer's reader
    # installed must agree on acceptance and on the bytes of every document.
    $extracted = Join-Path $work 'consumer-packages\multi.good\1.0.0'
    Push-Location $consumer
    try {
        $extractedValidation = & dotnet tool run agentdocs validate $extracted --strict 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Validator rejected NuGet's extracted copy: $($extractedValidation | Out-String)" }
    }
    finally { Pop-Location }
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $Feed 'Multi.Good.1.0.0.nupkg'))
    try {
        foreach ($installed in 'start.md', 'cookbook.md', 'http.md') {
            $entry = $zip.GetEntry("guide/$installed")
            $stream = $entry.Open()
            try {
                $memory = [System.IO.MemoryStream]::new()
                $stream.CopyTo($memory)
                $packedBytes = $memory.ToArray()
            }
            finally { $stream.Dispose() }
            # The tool installs canonical text (no BOM, LF), so compare canonical content, not raw bytes.
            function Get-CanonicalHash([byte[]] $bytes) {
                $text = [System.Text.UTF8Encoding]::new($false).GetString($bytes).TrimStart([char] 0xFEFF).Replace("`r`n", "`n")
                [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($text)))
            }
            $packed = Get-CanonicalHash $packedBytes
            $onDisk = Get-CanonicalHash ([System.IO.File]::ReadAllBytes((Join-Path $extracted "guide\$installed")))
            $delivered = Get-CanonicalHash ([System.IO.File]::ReadAllBytes((Join-Path $consumer ".agentdocs\packages\multi.good\guide\$installed")))
            if ($packedBytes.Length -ne [System.IO.File]::ReadAllBytes((Join-Path $extracted "guide\$installed")).Length) {
                throw "NuGet's extracted guide/$installed differs in size from the nupkg entry."
            }
            if ($packed -ne $onDisk -or ($installed -ne 'start.md' -and $packed -ne $delivered)) {
                throw "Bytes of guide/$installed differ unexpectedly between the nupkg, NuGet's extraction and the installed copy."
            }
        }
    }
    finally { $zip.Dispose() }
    $index = Get-Content -LiteralPath (Join-Path $consumer '.agentdocs\README.md') -Raw
    if ($index -notmatch 'guide/start\.md' -or $index -notmatch 'guide/cookbook\.md' -or $index -match 'guide/http\.md') {
        throw 'Consumer index must list the required and on-demand documents, and not the supporting one.'
    }

    $multiInvalid = [ordered]@{
        'DirAlias' = @{ Items = '<PackageGuidanceItem Include="start.md" PackagePath="Docs/one.md" Usage="required" Description="x" /><PackageGuidanceItem Include="http.md" PackagePath="docs/two.md" Description="y" />'; Match = 'portable aliases' }
        'DocAsDirectory' = @{ Items = '<PackageGuidanceItem Include="start.md" PackagePath="guide.md" Usage="required" Description="x" /><PackageGuidanceItem Include="http.md" PackagePath="guide.md/inner.md" Description="y" />'; Match = 'also used as a directory' }
        'Duplicate' = @{ Items = ($validItems + '<PackageGuidanceItem Include="http.md" PackagePath="guide/start.md" Usage="supporting" />'); Match = 'listed more than once' }
        'NoDescription' = @{ Items = '<PackageGuidanceItem Include="start.md" PackagePath="guide/start.md" Usage="required" />'; Match = 'not blank' }
        'OnlySupporting' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="guide/http.md" Usage="supporting" />'; Match = 'at least one required or onDemand' }
        'BadUsage' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="guide/http.md" Usage="always" Description="x" />'; Match = 'Usage must be required, onDemand or supporting' }
        'BadPath' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="../http.md" Description="x" />'; Match = 'portable relative Markdown path' }
        'HiddenDocumentPath' = @{ Items = '<PackageGuidanceItem Include="http.md" PackagePath="guide/.github/http.md" Description="x" />'; Match = 'portable relative Markdown path' }
        'Missing' = @{ Items = '<PackageGuidanceItem Include="nope.md" PackagePath="guide/nope.md" Description="x" />'; Match = 'missing guidance document' }
        'LongSupportingDescription' = @{ Items = ($validItems + '<PackageGuidanceItem Include="http.md" PackagePath="guide/other.md" Usage="supporting" Description="' + ('x' * 201) + '" />'); Match = 'at most 200 characters' }
        'BothForms' = @{ Items = $validItems; Extra = '<PackageGuidancePath>guide/x.md</PackageGuidancePath>'; Match = 'not both' }
        'BadReferencePath' = @{ Items = ($validItems + '<PackageGuidanceReference Include="../escape.md" PackageId="Independent.Publisher" DocumentPath="guides/overview.md" />'); Match = 'portable relative Markdown path' }
        'HiddenReferencePath' = @{ Items = ($validItems + '<PackageGuidanceReference Include=".github/other.md" PackageId="Independent.Publisher" DocumentPath="guides/overview.md" />'); Match = 'portable relative Markdown path' }
        'BadReferencePackage' = @{ Items = ($validItems + '<PackageGuidanceReference Include="guide/other.md" PackageId="bad package id" DocumentPath="guides/overview.md" />'); Match = 'valid NuGet package ID' }
        'BadReferenceDocument' = @{ Items = ($validItems + '<PackageGuidanceReference Include="guide/other.md" PackageId="Independent.Publisher" DocumentPath="../escape.md" />'); Match = 'DocumentPath must be a portable' }
        'HiddenReferenceDocument' = @{ Items = ($validItems + '<PackageGuidanceReference Include="guide/other.md" PackageId="Independent.Publisher" DocumentPath="guides/.github/overview.md" />'); Match = 'DocumentPath must be a portable' }
        'ReferenceCollision' = @{ Items = ($validItems + '<PackageGuidanceReference Include="guide/start.md" PackageId="Independent.Publisher" DocumentPath="guides/overview.md" />'); Match = 'collides with another' }
        'SelfReference' = @{ Items = ($validItems + '<PackageGuidanceReference Include="guide/self.md" PackageId="Multi.BadSelfReference" DocumentPath="guide/start.md" />'); Match = 'must not target the publishing package itself' }
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
    if ($null -eq $originalNuGetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $originalNuGetPackages
    }
    if ($null -eq $originalDotNetCliHome) {
        Remove-Item Env:DOTNET_CLI_HOME -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_CLI_HOME = $originalDotNetCliHome
    }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
exit 0
