param([string]$Configuration = 'Release',[string]$RuntimeVersion='10.0.11')
$ErrorActionPreference = 'Stop'
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskVersion = [string]([xml](Get-Content -LiteralPath (Join-Path $deskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($deskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Set the Desk release version in Directory.Build.props.' }
$deskOutput = Join-Path $deskRoot ('dist/UnityBridgeDesk-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$deskPreviousEnvironment = @{}
$deskEnvironment = @{ DOTNET_CLI_HOME=(Join-Path $deskRoot '.cache/dotnet'); NUGET_PACKAGES=$(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $deskRoot '.cache/nuget' }); DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_GENERATE_ASPNET_CERTIFICATE='false' }
Push-Location -LiteralPath $deskRoot
try {
    foreach ($entry in $deskEnvironment.GetEnumerator()) { $deskPreviousEnvironment[$entry.Key]=[Environment]::GetEnvironmentVariable($entry.Key,'Process'); [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') }
    foreach ($component in @('Desktop','Worker')) {
        $deskDestination = if ($component -eq 'Desktop') { $deskOutput } else { Join-Path $deskOutput 'worker' }
        & dotnet publish "src/UnityBridgeDesk.$component/UnityBridgeDesk.$component.csproj" -c $Configuration -r win-x64 --self-contained true -o $deskDestination -p:RestoreLockedMode=true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:ContinuousIntegrationBuild=true "-p:PathMap=$deskRoot=/_/UnityBridgeDesk"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $component" }
    }
    Copy-Item -LiteralPath (Join-Path $deskRoot 'build/DISTRIBUTION_README.md') -Destination (Join-Path $deskOutput 'README.md')
    Copy-Item -LiteralPath (Join-Path $deskRoot 'build/Compare-OfficialUnity.ps1') -Destination (Join-Path $deskOutput 'Compare-OfficialUnity.ps1')
    Copy-Item -LiteralPath (Join-Path $deskRoot 'README.md') -Destination (Join-Path $deskOutput 'BENCHMARK-README.md')
    # Include the same evidence-based README and its relative image/link targets in the binary release.
    New-Item -ItemType Directory -Path (Join-Path $deskOutput 'design/branding') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $deskRoot 'design/landscape-retouch-prompts.json') -Destination (Join-Path $deskOutput 'design/landscape-retouch-prompts.json')
    Copy-Item -LiteralPath (Join-Path $deskRoot 'design/branding/desk-icon.png') -Destination (Join-Path $deskOutput 'design/branding/desk-icon.png')
    Copy-Item -LiteralPath (Join-Path $deskRoot 'docs') -Destination (Join-Path $deskOutput 'docs') -Recurse
    # Historical source links remain useful in the repository; the binary bundle shows them as paths.
    $deskArchivedDoc = Join-Path $deskOutput 'docs/BENCHMARK-METHODOLOGY.md'
    $deskArchivedText = Get-Content -LiteralPath $deskArchivedDoc -Raw
    $deskArchivedText = [regex]::Replace($deskArchivedText,'\[([^\]]+)\]\((\.\./src/[^)]+)\)','$1 (`$2`)')
    ("> 실행용 ZIP에는 소스 코드를 동봉하지 않습니다. 아래 코드 경로는 GitHub 소스 폴더를 기준으로 확인하세요.`n`n" + $deskArchivedText) | Set-Content -LiteralPath $deskArchivedDoc -Encoding utf8
    foreach($deskNotice in @('LICENSE','THIRD_PARTY_NOTICES.md','CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $deskRoot $deskNotice) -Destination (Join-Path $deskOutput $deskNotice)
    }
    $deskNotices=Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64/$RuntimeVersion"
    foreach($deskNotice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')){if(Test-Path -LiteralPath (Join-Path $deskNotices $deskNotice)){Copy-Item -LiteralPath (Join-Path $deskNotices $deskNotice) -Destination (Join-Path $deskOutput $deskNotice)}}
    $deskManifest = Get-ChildItem -LiteralPath $deskOutput -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path=[IO.Path]::GetRelativePath($deskOutput,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    @{ formatVersion=1; version=$deskVersion; platform='win-x64'; selfContained=$true; runtime=$RuntimeVersion; sdk=(& dotnet --version); files=@($deskManifest) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $deskOutput 'distribution.json') -Encoding utf8
    Write-Output $deskOutput
}
finally {
    foreach ($entry in $deskPreviousEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') }
    Pop-Location
}
