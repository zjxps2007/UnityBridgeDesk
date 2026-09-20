param([string]$OutputDirectory, [switch]$DeskOnly = $true)
$ErrorActionPreference = 'Stop'
$deskRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $deskRoot ('dist/UnityBridgeDesk-source-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$deskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $deskOutput) { throw 'OutputDirectory must be a new directory. Existing files are never deleted or overwritten.' }
if ($deskOutput.StartsWith($deskRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and
    -not $deskOutput.StartsWith((Join-Path $deskRoot 'dist') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inside this repository, source exports must be placed under dist/.'
}
$deskArchive = $deskOutput + '.zip'
if (Test-Path -LiteralPath $deskArchive) { throw 'An archive already exists at the destination.' }

# Explicit roots prevent local run data or design history from becoming public by accident.
$deskRootFiles = @('.gitignore','.gitattributes','.editorconfig','AGENTS.md','README.md','CHANGELOG.md','LICENSE','CONTRIBUTING.md',
    'THIRD_PARTY_NOTICES.md','Directory.Build.props','global.json','NuGet.Config','UnityBridgeDesk.slnx')
$deskBuildFiles = @('Verify.ps1','StartDesk.ps1','Publish-Desk.ps1','Package-Desk.ps1','Verify-Package.ps1',
    'Export-Source.ps1','Compare-OfficialUnity.ps1','Build-Icon.ps1','DeskLauncher.c','DeskLauncher.rc','DISTRIBUTION_README.md',
    'Publish-Companion.ps1','Verify-Companion.ps1')
if ($DeskOnly) { $deskBuildFiles = @($deskBuildFiles | Where-Object { $_ -notin @('Publish-Companion.ps1','Verify-Companion.ps1') }) }
$deskSelected = [Collections.Generic.List[string]]::new()
foreach ($deskRelative in $deskRootFiles) { $deskSelected.Add($deskRelative) }
foreach ($deskName in $deskBuildFiles) { $deskSelected.Add('build/' + $deskName) }
foreach ($deskName in @('UI-DESIGN-WORKFLOW.md','UI-REFRESH-REFERENCES.md','UI-APPLICATION-20260919.md','MOTION-APPLICATION-20260919.md','AI-BENCH-UI-PROPOSAL.md','landscape-retouch-prompts.json')) { $deskSelected.Add('design/' + $deskName) }

function Add-DeskSources([string]$Relative) {
    $deskDirectory = Join-Path $deskRoot $Relative
    if ((Get-Item -LiteralPath $deskDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source directory: $Relative" }
    foreach ($deskItem in Get-ChildItem -LiteralPath $deskDirectory -Force) {
        if ($deskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source item: $($deskItem.Name)" }
        $deskChild = $Relative + '/' + $deskItem.Name
        if ($DeskOnly -and $deskChild -in @('src/UnityBridgeDesk.Companion','tests/UnityBridgeDesk.Companion.Tests')) { continue }
        if ($deskItem.PSIsContainer) {
            if ($deskItem.Name -notin @('bin','obj','.cache','.git','.vs','.idea','.vscode','TestResults','node_modules')) { Add-DeskSources $deskChild }
        } elseif ($deskItem.Extension -in @('.cs','.csproj','.props','.xaml','.manifest','.json','.md','.yml','.yaml','.png','.gif','.ico','.txt')) {
            if ($deskItem.Name -notin @('auth.json','credentials.json') -and $deskItem.Name -notlike '*.local.json') { $deskSelected.Add($deskChild) }
        }
    }
}
foreach ($deskDirectory in @('src','tests','docs','.github','design/branding')) { Add-DeskSources $deskDirectory }

# Validate the complete selection before writing any export file.
foreach ($deskRelative in $deskSelected) {
    $deskSource = Join-Path $deskRoot $deskRelative
    $deskInfo = Get-Item -LiteralPath $deskSource -Force
    if ($deskInfo.PSIsContainer -or ($deskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Not a regular source file: $deskRelative" }
}
New-Item -ItemType Directory -Path $deskOutput | Out-Null
foreach ($deskRelative in $deskSelected) {
    $deskTarget = Join-Path $deskOutput $deskRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $deskTarget) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $deskRoot $deskRelative) -Destination $deskTarget
}

if ($DeskOnly) {
    $solutionPath = Join-Path $deskOutput 'UnityBridgeDesk.slnx'
    $solution = [xml](Get-Content -LiteralPath $solutionPath -Raw)
    foreach ($project in @($solution.SelectNodes('//Project'))) {
        if ($project.Path -match 'UnityBridgeDesk\.Companion') { $null = $project.ParentNode.RemoveChild($project) }
    }
    $solution.Save($solutionPath)
    $verifyPath = Join-Path $deskOutput 'build/Verify.ps1'
    [IO.File]::WriteAllText($verifyPath, [IO.File]::ReadAllText($verifyPath).Replace("'Core', 'Infrastructure', 'Desktop', 'Companion'", "'Core', 'Infrastructure', 'Desktop'"))
    # Keep this exporter usable from the independent Desk repository as well.
    $exportPath = Join-Path $deskOutput 'build/Export-Source.ps1'
    $exportText = [IO.File]::ReadAllText($exportPath).Replace('[switch]$DeskOnly = $true)', '[switch]$DeskOnly = $true)')
    [IO.File]::WriteAllText($exportPath, $exportText)
    $agentsPath = Join-Path $deskOutput 'AGENTS.md'
    $agents = [IO.File]::ReadAllText($agentsPath).Replace('현재 프로젝트 경로는 `src/UnityBridgeDesk.Companion`이다.', '리틀 모델 엔진 소스는 별도 저장소에서 관리하며 이 저장소에 포함하지 않는다.')
    [IO.File]::WriteAllText($agentsPath, $agents)
    $developmentPath = Join-Path $deskOutput 'docs/DEVELOPMENT.md'
    $development = [IO.File]::ReadAllText($developmentPath)
    $development = [regex]::Replace($development, '(?s)## 별도 바탕화면 동반자.*?(?=## 환경과 기본 실행)', "## 별도 바탕화면 동반자`n`n리틀 모델 엔진은 별도 저장소와 실행용 ZIP으로 배포한다. 이 Desk 소스에는 동반자 실행 프로젝트·검사·배포 스크립트를 포함하지 않는다. 화면 안의 아라·아샤는 Desk 자체 파일을 사용하며 서로 자동 동기화하지 않는다.`n`n")
    [IO.File]::WriteAllText($developmentPath, $development)
    $companionDoc = Join-Path $deskOutput 'docs/COMPANION.md'
    [IO.File]::WriteAllText($companionDoc, "# 리틀 모델 엔진`n`n리틀 모델 엔진은 고양이 아라와 여우 아샤가 작업표시줄과 열린 창의 윗테두리를 돌아다니는 별도 Windows 앱이다. 별도로 배포하는 LittleModelEngine.exe를 실행한다.`n`n이 Desk 저장소에는 독립 앱 소스나 실행 파일을 포함하지 않는다. 리틀 모델 엔진 소스 묶음의 README에서 설치·개발 방법을 확인한다. 두 앱의 그림·동작·설정·실행 상태는 자동으로 동기화하지 않는다.`n")
}

# .NET's ZIP writer preserves dotfiles such as .gitignore and .github on Windows.
[IO.Compression.ZipFile]::CreateFromDirectory($deskOutput,$deskArchive,[IO.Compression.CompressionLevel]::Optimal,$true)
$deskZip = [IO.Compression.ZipFile]::OpenRead($deskArchive)
try {
    $deskPrefix = (Split-Path -Leaf $deskOutput) + '/'
    $deskEntries = @($deskZip.Entries | Where-Object Name)
    if ($deskEntries.Count -ne $deskSelected.Count) { throw 'Source ZIP file count mismatch.' }
    foreach ($deskEntry in $deskEntries) {
        if (-not $deskEntry.FullName.StartsWith($deskPrefix,[StringComparison]::Ordinal)) { throw 'Unexpected source archive root.' }
        $deskRelative = $deskEntry.FullName.Substring($deskPrefix.Length)
        if ($deskRelative -notin $deskSelected) { throw 'Unexpected source archive file.' }
        $deskStream = $deskEntry.Open()
        try { $deskHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($deskStream)) }
        finally { $deskStream.Dispose() }
        if ($deskHash -ne (Get-FileHash -LiteralPath (Join-Path $deskOutput $deskRelative) -Algorithm SHA256).Hash) {
            throw "Source archive hash mismatch: $deskRelative"
        }
    }
}
finally { $deskZip.Dispose() }
Write-Output "Source export verified: $($deskSelected.Count) files."
Write-Output $deskOutput
Write-Output $deskArchive
