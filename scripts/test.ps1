[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$TestProjectPath = Join-Path $RepositoryRoot 'ChromiumWall.Tests\ChromiumWall.Tests.csproj'
$DotnetArguments = @('run', '--project', $TestProjectPath, '--configuration', $Configuration) + $Arguments

Push-Location $RepositoryRoot
try {
    & dotnet @DotnetArguments
    $ExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $ExitCode
