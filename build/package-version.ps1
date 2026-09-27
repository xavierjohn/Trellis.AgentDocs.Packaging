function Get-NuGetPackageVersion {
    param([string] $Project)

    $output = & dotnet msbuild $Project -target:GetBuildVersion `
        -getProperty:NuGetPackageVersion -verbosity:quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot determine NuGet package version for $Project`: $($output | Out-String)"
    }
    $version = ($output | Out-String).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Invalid NuGet package version for $Project`: $version"
    }
    return $version
}
