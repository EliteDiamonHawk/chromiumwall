[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputPath = 'publish',
    [switch] $NoRestore,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'ChromiumWall\ChromiumWall.csproj'
if (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $RepositoryRoot $OutputPath
}
$DotnetArguments = @('publish', $ProjectPath, '--configuration', $Configuration, '--output', $OutputPath)
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
