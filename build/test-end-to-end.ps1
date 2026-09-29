[CmdletBinding()]
param(
    [string] $WorkDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "agentdocs-e2e-$PID"),
    [switch] $KeepSample
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'package-version.ps1')
$feed = Join-Path $WorkDirectory 'feed'
$repo = Join-Path $WorkDirectory 'consumer'
$app = Join-Path $repo 'backend' 'app'
$appProject = Join-Path 'backend' 'app' 'App.csproj'
$otherProject = Join-Path 'backend' 'other' 'Other.csproj'
$nestedSolution = Join-Path 'backend' 'Nested.slnx'
$toolManifest = Join-Path $repo '.config' 'dotnet-tools.json'
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

function Get-GuideDescription {
    param([string] $Version)
    if ($KeepSample -and $Version -eq '2.1.0') { return "Select the sample publisher's integration mode." }
    return "Read before using the sample publisher (version $Version)."
}

function Publish-Version {
    param([string] $Version, [bool] $WithGuide)
    $description = Get-GuideDescription $Version
    $props = if ($WithGuide) {
        '<PackageGuidanceDocument>$(MSBuildProjectDirectory)/overview.md</PackageGuidanceDocument><PackageGuidancePath>guides/overview.md</PackageGuidancePath>' +
        "<PackageGuidanceUsage>required</PackageGuidanceUsage><PackageGuidanceDescription>$([System.Security.SecurityElement]::Escape($description))</PackageGuidanceDescription>"
    } else { '' }
    $reference = if ($WithGuide) {
        "<PackageReference Include=`"Trellis.AgentDocs.Packaging`" Version=`"$helperVersion`" PrivateAssets=`"all`" />"
    } else { '' }
    $guide = if ($KeepSample -and $Version -eq '2.1.0') {
        "# Integration routing`n`n" +
        "For the sample consumer, the supported integration mode is ``Copper Lantern``.`n"
    } else { "# Guide $Version`n" }
    [System.IO.File]::WriteAllText((Join-Path $publisher 'overview.md'), $guide)
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

function Set-Policy {
    param([string[]] $Approved)
    $list = ($Approved | ForEach-Object { '"' + $_ + '"' }) -join ','
    [System.IO.File]::WriteAllText((Join-Path $repo '.agentdocs\policy.json'),
        '{"schemaVersion":1,"approvedPackages":[' + $list + ']}')
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
        Invoke-Dotnet 'Initial restore' @('restore', $appProject, '--nologo', '-v:q') | Out-Null
        Invoke-Dotnet 'Tool manifest' @('new', 'tool-manifest', '--output', '.config') | Out-Null
        Invoke-Dotnet 'Install tool' @('tool', 'install', 'Trellis.AgentDocs', '--version', $toolVersion,
            '--add-source', $feed, '--tool-manifest', $toolManifest) | Out-Null
        $installed = Join-Path $repo '.agentdocs\packages\independent.publisher\guides\overview.md'

        # A package the consumer never approved is pending: listed by name only, never read.
        $initOutput = Invoke-Dotnet 'Init' @('tool', 'run', 'agentdocs', 'init', $appProject)
        if (Test-Path -LiteralPath $installed) { throw 'Init installed a guide from an unapproved package.' }
        if (($initOutput | Out-String) -notmatch 'approvedPackages' -or
            ($initOutput | Out-String) -notmatch '"Independent.Publisher"') {
            throw "Init did not print the approval line for the pending package:`n$($initOutput | Out-String)"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $repo '.agentdocs\policy.json'))) { throw 'Init did not create the policy.' }
        if ((Get-Content -LiteralPath (Join-Path $repo '.agentdocs\README.md') -Raw) -notmatch 'Pending review') {
            throw 'The index does not list the pending package.'
        }
        if ((Test-Path (Join-Path $repo 'Directory.Build.targets')) -or
            (Test-Path (Join-Path $repo 'Directory.Solution.targets'))) {
            throw 'Init installed restore hooks.'
        }

        Set-Policy @('Independent.Publisher')
        Invoke-Dotnet 'Approve and sync' @('tool', 'run', 'agentdocs', 'sync') | Out-Null
        if ((Get-Content -LiteralPath $installed -Raw) -notmatch 'Guide 1.0.0') {
            throw 'Approved guide was not installed.'
        }
        Invoke-Dotnet 'Initial check' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        Invoke-Dotnet 'Explicit sync with restore' @('tool', 'run', 'agentdocs', 'sync', '--restore') | Out-Null
        Invoke-Dotnet 'Initial solution' @('new', 'sln', '-n', 'Consumer', '--format', 'slnx') | Out-Null
        Invoke-Dotnet 'Add solution project' @('sln', 'Consumer.slnx', 'add', $appProject) | Out-Null

        # A restore alone never changes guidance; the developer runs sync after a package change.
        Publish-Version '2.0.0' $true
        Select-Package '2.0.0'
        Invoke-Dotnet 'Upgraded project restore' @('restore', $appProject, '--nologo', '-v:q') | Out-Null
        if ((Get-Content -LiteralPath $installed -Raw) -notmatch 'Guide 1.0.0') {
            throw 'Restore changed the installed guide without an explicit sync.'
        }
        & dotnet tool run agentdocs check 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { throw 'Check did not report the stale guide after the package upgrade.' }
        Invoke-Dotnet 'Sync after upgrade' @('tool', 'run', 'agentdocs', 'sync') | Out-Null
        if ((Get-Content -LiteralPath $installed -Raw) -notmatch 'Guide 2.0.0') {
            throw 'Sync did not install the upgraded guide.'
        }
        Invoke-Dotnet 'Upgraded check' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        $other = Join-Path $repo 'backend\other'
        New-Item -ItemType Directory -Path $other | Out-Null
        Copy-Item -LiteralPath (Join-Path $app 'App.csproj') -Destination (Join-Path $other 'Other.csproj')
        Select-Package '2.0.0'
        Invoke-Dotnet 'Add second solution project' @('sln', 'Consumer.slnx',
            'add', $otherProject) | Out-Null
        Invoke-Dotnet 'Restore two-project solution' @('restore', 'Consumer.slnx', '--nologo', '-v:q') | Out-Null
        Invoke-Dotnet 'Remove project graph' @('tool', 'run', 'agentdocs', 'remove') | Out-Null
        if (-not (Test-Path -LiteralPath (Join-Path $repo '.agentdocs\policy.json'))) { throw 'Remove deleted the consumer policy.' }
        Invoke-Dotnet 'Select two-project graph' @('tool', 'run', 'agentdocs', 'init', 'Consumer.slnx') | Out-Null
        Invoke-Dotnet 'Check two-project graph' @('tool', 'run', 'agentdocs', 'check') | Out-Null
        Invoke-Dotnet 'Nested solution' @('new', 'sln', '-n', 'Nested', '--format', 'slnx',
            '-o', 'backend') | Out-Null
        Invoke-Dotnet 'Add nested solution project' @('sln', $nestedSolution,
            'add', $appProject) | Out-Null
        Invoke-Dotnet 'Add nested second project' @('sln', $nestedSolution,
            'add', $otherProject) | Out-Null
        Publish-Version '2.1.0' $true
        Select-Package '2.1.0'
        Invoke-Dotnet 'Nested solution restore' @('restore', $nestedSolution, '--nologo', '-v:q') | Out-Null
        Invoke-Dotnet 'Remove root solution graph' @('tool', 'run', 'agentdocs', 'remove') | Out-Null
        Invoke-Dotnet 'Select nested solution graph' @('tool', 'run', 'agentdocs', 'init', $nestedSolution) | Out-Null
        if (-not (Get-Content -LiteralPath $installed -Raw).Contains(
            $(if ($KeepSample) { 'Copper Lantern' } else { 'Guide 2.1.0' }))) {
            throw 'Nested solution init did not install the upgraded guide.'
        }
        $rootInstructions = Join-Path $repo 'AGENTS.md'
        $copilotInstructions = Join-Path $repo '.github\copilot-instructions.md'
        $nestedInstructions = Join-Path $repo 'backend\AGENTS.md'
        $index = Join-Path $repo '.agentdocs\README.md'
        foreach ($pointer in @(
            @{ Path = $rootInstructions; Guide = '.agentdocs/README.md' },
            @{ Path = $copilotInstructions; Guide = '../.agentdocs/README.md' },
            @{ Path = $nestedInstructions; Guide = '../.agentdocs/README.md' }
        )) {
            $text = Get-Content -LiteralPath $pointer.Path -Raw
            if (-not $text.Contains("**Read ``$($pointer.Guide)`` now.**") -or
                -not $text.Contains('The path is relative to this instruction file')) {
                throw "Missing consistent pointer in $($pointer.Path)."
            }
        }
        $indexText = Get-Content -LiteralPath $index -Raw
        if (-not $indexText.Contains('## Required reading by project') -or
            -not $indexText.Contains('### Group 1') -or
            -not $indexText.Contains('Independent.Publisher') -or
            -not $indexText.Contains('.agentdocs/packages/independent.publisher/guides/overview.md') -or
            -not $indexText.Contains((Get-GuideDescription '2.1.0'))) {
            throw 'The installed guidance index lacks required-reading groups or a guide description.'
        }
        if ($KeepSample) {
            Invoke-Dotnet 'Sample check' @('tool', 'run', 'agentdocs', 'check') | Out-Null
            Write-Host "Sample consumer: $repo"
            Write-Host 'Only local packages were packed and installed; nothing was published.'
            return
        }
        [System.IO.File]::AppendAllText($rootInstructions,
            "`nSee [old instructions](backend/.github/old.md).`n")
        $syncOutput = Invoke-Dotnet 'Sync with stale link' @('tool', 'run', 'agentdocs', 'sync')
        if (($syncOutput | Out-String) -notmatch 'AGENTS.md:\d+: local link `backend/.github/old.md` does not exist') {
            throw 'Sync did not warn about the stale handwritten link.'
        }
        $snapshots = @($rootInstructions, $copilotInstructions, $nestedInstructions, $index) |
            ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
        Invoke-Dotnet 'Repeated sync' @('tool', 'run', 'agentdocs', 'sync') | Out-Null
        $again = @($rootInstructions, $copilotInstructions, $nestedInstructions, $index) |
            ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
        if (($snapshots -join ',') -ne ($again -join ',')) {
            throw 'A second sync modified an unchanged guidance context.'
        }
        $strictOutput = & dotnet tool run agentdocs sync --strict 2>&1
        if ($LASTEXITCODE -eq 0 -or ($strictOutput | Out-String) -notmatch 'AGENTS.md:\d+') {
            throw 'Strict sync did not reject the stale handwritten link.'
        }
        Publish-Version '3.0.0' $false
        Select-Package '3.0.0'
        Invoke-Dotnet 'Solution restore without guide' @('restore', '--nologo', '-v:m') | Out-Null
        $output = Invoke-Dotnet 'Sync without guide' @('tool', 'run', 'agentdocs', 'sync')
        if (($output | Out-String) -notmatch 'no longer publishes guidance' -or
            (Test-Path -LiteralPath $installed)) {
            throw "Sync did not report and remove the missing guide:`n$($output | Out-String)"
        }
        Invoke-Dotnet 'Removed-guide check' @('tool', 'run', 'agentdocs', 'check') | Out-Null

        # An invalid manifest never fails restore; it fails an explicit sync for an approved package only.
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
                    ('0' * 64) + '","usage":"required","description":"Broken."}]}')
            }
            finally { $writer.Dispose() }
        }
        finally { $archive.Dispose() }
        Select-Package '4.0.0'
        Invoke-Dotnet 'Restore with invalid guide' @('restore', '--nologo', '-v:m') | Out-Null
        $invalidOutput = & dotnet tool run agentdocs sync 2>&1
        if ($LASTEXITCODE -eq 0 -or ($invalidOutput | Out-String) -notmatch 'SHA-256 mismatch') {
            throw "Invalid guide manifest did not fail an approved sync:`n$($invalidOutput | Out-String)"
        }
        if (Test-Path -LiteralPath $installed) { throw 'Invalid guide was installed.' }
        Set-Policy @()
        $pendingOutput = Invoke-Dotnet 'Sync with the invalid package unapproved' @('tool', 'run', 'agentdocs', 'sync')
        if (($pendingOutput | Out-String) -notmatch 'pending Independent.Publisher') {
            throw "An unapproved invalid package was not reported as pending:`n$($pendingOutput | Out-String)"
        }
        Invoke-Dotnet 'Remove' @('tool', 'run', 'agentdocs', 'remove') | Out-Null
        if ((Test-Path (Join-Path $repo 'Directory.Build.targets')) -or
            (Test-Path (Join-Path $repo 'Directory.Solution.targets'))) {
            throw 'Remove left restore hooks behind.'
        }
    }
    finally { Pop-Location }
    Write-Host 'PASS pending-by-default install, approval, explicit sync after upgrades, two-project and nested solutions, missing-guide notice, invalid-guide isolation, and cleanup.'
    $succeeded = $true
}
finally {
    $env:DOTNET_CLI_HOME = $oldHome
    $env:NUGET_PACKAGES = $oldPackages
    if ($succeeded -and -not $KeepSample) { Remove-Item -LiteralPath $WorkDirectory -Recurse -Force }
}
