[CmdletBinding()]
param([string] $WorkDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "agentdocs-e2e-$PID"))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'package-version.ps1')
$feed = Join-Path $WorkDirectory 'feed'
$repo = Join-Path $WorkDirectory 'consumer'
$app = Join-Path $repo 'backend\app'
$publisher = Join-Path $WorkDirectory 'publisher'
$helperVersion = ''
$toolVersion = ''
if (Test-Path -LiteralPath $WorkDirectory) { throw "Probe directory already exists: $WorkDirectory" }
New-Item -ItemType Directory -Path $feed, $app, $publisher -Force | Out-Null

function Invoke-Dotnet {
    param([string] $Stage, [string[]] $Arguments)
    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Stage failed:`n$($output | Out-String)" }
    return $output
}

function Publish-Version {
    param([string] $Version, [bool] $WithGuide)
    $props = if ($WithGuide) {
        '<PackageGuidanceDocument>$(MSBuildProjectDirectory)/overview.md</PackageGuidanceDocument><PackageGuidancePath>guides/overview.md</PackageGuidancePath>'
    } else { '' }
    $reference = if ($WithGuide) {
        "<PackageReference Include=`"Trellis.AgentDocs.Packaging`" Version=`"$helperVersion`" PrivateAssets=`"all`" />"
    } else { '' }
    [System.IO.File]::WriteAllText((Join-Path $publisher 'overview.md'), "# Guide $Version`n")
    [System.IO.File]::WriteAllText((Join-Path $publisher 'Publisher.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Independent.Publisher</PackageId>
    <Version>$Version</Version><IncludeBuildOutput>false</IncludeBuildOutput><NoWarn>NU5128</NoWarn>$props</PropertyGroup>
  <ItemGroup>
    <None Include="overview.md" Pack="true" PackagePath="publisher.txt" />
    $reference
  </ItemGroup>
</Project>
"@)
    Invoke-Dotnet "Publisher $Version restore" @('restore', (Join-Path $publisher 'Publisher.csproj'),
        '--source', $feed, '--nologo', '-v:q') | Out-Null
    Invoke-Dotnet "Publisher $Version pack" @('pack', (Join-Path $publisher 'Publisher.csproj'),
        '--no-restore', '-o', $feed, '--nologo', '-v:q') | Out-Null
}

function Select-Package {
    param([string] $Version)
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><PackageReference Include="Independent.Publisher" Version="$Version" /></ItemGroup>
</Project>
"@
    [System.IO.File]::WriteAllText((Join-Path $app 'App.csproj'), $project)
    $other = Join-Path $repo 'backend\other\Other.csproj'
    if (Test-Path -LiteralPath $other) {
        [System.IO.File]::WriteAllText($other, $project)
    }
}

$oldHome = $env:DOTNET_CLI_HOME
$oldPackages = $env:NUGET_PACKAGES
$succeeded = $false
try {
    Invoke-Dotnet 'Pack helper and publisher' @('pack', (Join-Path $root 'src\Trellis.AgentDocs.Packaging.csproj'),
        '-c', 'Release', '-o', $feed, '--nologo', '-v:q') | Out-Null
    Invoke-Dotnet 'Pack tool' @('pack', (Join-Path $root 'Trellis.AgentDocs\src\Trellis.AgentDocs.csproj'),
        '-c', 'Release', '-o', $feed, '--nologo', '-v:q') | Out-Null
    $helperVersion = Get-NuGetPackageVersion (Join-Path $root 'src\Trellis.AgentDocs.Packaging.csproj')
    $toolVersion = Get-NuGetPackageVersion (Join-Path $root 'Trellis.AgentDocs\src\Trellis.AgentDocs.csproj')
    if ($helperVersion -ne $toolVersion -or
        -not (Test-Path -LiteralPath (Join-Path $feed "Trellis.AgentDocs.Packaging.$helperVersion.nupkg")) -or
        -not (Test-Path -LiteralPath (Join-Path $feed "Trellis.AgentDocs.$toolVersion.nupkg"))) {
        throw 'The helper and tool must pack with the same NBGV version.'
    }
    $env:DOTNET_CLI_HOME = Join-Path $WorkDirectory 'cli-home'
    $env:NUGET_PACKAGES = Join-Path $WorkDirectory 'packages'
    Publish-Version '1.0.0' $true
    Invoke-Dotnet 'Git init' @('--version') | Out-Null
    & git -C $repo init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Git init failed.' }
    [System.IO.File]::WriteAllText((Join-Path $repo 'NuGet.Config'), @"
<configuration><packageSources><clear/><add key="local" value="$feed" /></packageSources></configuration>
"@)
    Select-Package '1.0.0'
    Push-Location $repo
    try {
        Invoke-Dotnet 'Initial restore' @('restore', 'backend\app\App.csproj', '--nologo', '-v:q') | Out-Null
        Invoke-Dotnet 'Tool manifest' @('new', 'tool-manifest', '--output', '.config') | Out-Null
        Invoke-Dotnet 'Install tool' @('tool', 'install', 'Trellis.AgentDocs', '--version', $toolVersion,
            '--add-source', $feed, '--tool-manifest', '.config\dotnet-tools.json') | Out-Null
        Invoke-Dotnet 'Init' @('tool', 'run', 'agentdocs', 'init', 'backend\app\App.csproj') | Out-Null
        $installed = Join-Path $repo '.agentdocs\packages\independent.publisher\guides\overview.md'
        if ((Get-Content -LiteralPath $installed -Raw) -notmatch 'Guide 1.0.0') {
            throw 'Initial guide was not installed.'
        }
        Invoke-Dotnet 'Initial check' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        Invoke-Dotnet 'Explicit sync with restore' @('tool', 'run', 'agentdocs', 'sync', '--restore') | Out-Null
        Invoke-Dotnet 'Initial solution' @('new', 'sln', '-n', 'Consumer', '--format', 'slnx') | Out-Null
        Invoke-Dotnet 'Add solution project' @('sln', 'Consumer.slnx', 'add', 'backend\app\App.csproj') | Out-Null
        Publish-Version '2.0.0' $true
        Select-Package '2.0.0'
        Invoke-Dotnet 'Upgraded project restore' @('restore', 'backend\app\App.csproj', '--nologo', '-v:q') | Out-Null
        if ((Get-Content -LiteralPath $installed -Raw) -notmatch 'Guide 2.0.0') {
            throw 'Project restore did not refresh the upgraded guide.'
        }
        Invoke-Dotnet 'Upgraded check' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        $other = Join-Path $repo 'backend\other'
        New-Item -ItemType Directory -Path $other | Out-Null
        Copy-Item -LiteralPath (Join-Path $app 'App.csproj') -Destination (Join-Path $other 'Other.csproj')
        Select-Package '2.0.0'
        Invoke-Dotnet 'Add second solution project' @('sln', 'Consumer.slnx',
            'add', 'backend\other\Other.csproj') | Out-Null
        Invoke-Dotnet 'Restore two-project solution' @('restore', 'Consumer.slnx', '--nologo', '-v:q') | Out-Null
        Invoke-Dotnet 'Remove project graph' @('tool', 'run', 'agentdocs', 'remove') | Out-Null
        Invoke-Dotnet 'Select two-project graph' @('tool', 'run', 'agentdocs', 'init', 'Consumer.slnx') | Out-Null
        Invoke-Dotnet 'Check two-project graph' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        Invoke-Dotnet 'Nested solution' @('new', 'sln', '-n', 'Nested', '--format', 'slnx',
            '-o', 'backend') | Out-Null
        Invoke-Dotnet 'Add nested solution project' @('sln', 'backend\Nested.slnx',
            'add', 'backend\app\App.csproj') | Out-Null
        Invoke-Dotnet 'Add nested second project' @('sln', 'backend\Nested.slnx',
            'add', 'backend\other\Other.csproj') | Out-Null
        Publish-Version '2.1.0' $true
        Select-Package '2.1.0'
        Invoke-Dotnet 'Nested solution restore' @('restore', 'backend\Nested.slnx', '--nologo', '-v:q') | Out-Null
        if ((Get-Content -LiteralPath $installed -Raw) -notmatch 'Guide 2.1.0') {
            throw 'Nested solution restore did not refresh the upgraded guide.'
        }
        Publish-Version '3.0.0' $false
        Select-Package '3.0.0'
        $output = Invoke-Dotnet 'Solution restore without guide' @('restore', '--nologo', '-v:m')
        if (($output | Out-String) -notmatch 'no longer publishes guidance' -or
            (Test-Path -LiteralPath $installed)) {
            throw "Solution restore did not report and remove the missing guide:`n$($output | Out-String)"
        }
        Invoke-Dotnet 'Removed-guide check' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        Publish-Version '4.0.0' $true
        $archive = [System.IO.Compression.ZipFile]::Open(
            (Join-Path $feed 'Independent.Publisher.4.0.0.nupkg'),
            [System.IO.Compression.ZipArchiveMode]::Update)
        try {
            $archive.GetEntry('guidance/reference-manifest.json').Delete()
            $invalid = $archive.CreateEntry('guidance/reference-manifest.json')
            $writer = [System.IO.StreamWriter]::new($invalid.Open())
            try {
                $writer.Write('{"schemaVersion":1,"documents":[{"path":"guides/overview.md","sha256":"' +
                    ('0' * 64) + '"}],"entryPoints":["guides/overview.md"]}')
            }
            finally { $writer.Dispose() }
        }
        finally { $archive.Dispose() }
        Select-Package '4.0.0'
        $invalidOutput = & dotnet restore --nologo -v:m 2>&1
        if ($LASTEXITCODE -eq 0 -or ($invalidOutput | Out-String) -notmatch 'SHA-256 mismatch') {
            throw "Invalid guide manifest did not fail restore:`n$($invalidOutput | Out-String)"
        }
        if (Test-Path -LiteralPath $installed) { throw 'Invalid guide was installed after failed restore.' }
        Invoke-Dotnet 'Remove' @('tool', 'run', 'agentdocs', 'remove') | Out-Null
        if ((Test-Path (Join-Path $repo 'Directory.Build.targets')) -or
            (Test-Path (Join-Path $repo 'Directory.Solution.targets'))) {
            throw 'Remove left restore hooks behind.'
        }
    }
    finally { Pop-Location }
    Write-Host 'PASS initial install, project/root/two-project nested solution restore refresh, missing-guide notice, invalid-guide failure, and cleanup.'
    $succeeded = $true
}
finally {
    $env:DOTNET_CLI_HOME = $oldHome
    $env:NUGET_PACKAGES = $oldPackages
    if ($succeeded) { Remove-Item -LiteralPath $WorkDirectory -Recurse -Force }
}
