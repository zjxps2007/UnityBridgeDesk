param(
    [switch]$Restore,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskEnvironment = @{
    DOTNET_CLI_HOME = (Join-Path $deskRoot '.cache/dotnet')
    NUGET_PACKAGES = (Join-Path $deskRoot '.cache/nuget')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
}
$deskPreviousEnvironment = @{}

function Invoke-DeskDotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Push-Location -LiteralPath $deskRoot
try {
    foreach ($entry in $deskEnvironment.GetEnumerator()) {
        $deskPreviousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if ($Restore) { Invoke-DeskDotnet @('restore', 'UnityBridgeDesk.slnx', '--locked-mode') }
    Invoke-DeskDotnet @('build', 'UnityBridgeDesk.slnx', '--no-restore', '--configuration', $Configuration)
    foreach ($component in @('Core', 'Infrastructure', 'Desktop')) {
        $project = "tests/UnityBridgeDesk.$component.Tests/UnityBridgeDesk.$component.Tests.csproj"
        Invoke-DeskDotnet @('test', $project, '--no-build', '--no-restore', '--configuration', $Configuration,
            '--logger', "trx;LogFileName=$component.$Configuration.trx", '--results-directory', 'TestResults')
    }
}
finally {
    foreach ($entry in $deskPreviousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    Pop-Location
}
