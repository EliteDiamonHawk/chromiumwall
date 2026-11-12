[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SolutionPath = Join-Path $RepositoryRoot 'ChromiumWall\ChromiumWall.sln'
$DotnetArguments = @('build', $SolutionPath, '--configuration', $Configuration)
if ($NoRestore) { $DotnetArguments += '--no-restore' }
$DotnetArguments += $Arguments

Push-Location $RepositoryRoot
try {
    & dotnet @DotnetArguments
    $ExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $ExitCode
