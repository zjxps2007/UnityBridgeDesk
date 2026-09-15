param([string]$OutputDirectory)
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
$deskRootFiles = @('.gitignore','.gitattributes','.editorconfig','README.md','CHANGELOG.md','LICENSE','CONTRIBUTING.md',
    'THIRD_PARTY_NOTICES.md','Directory.Build.props','global.json','NuGet.Config','UnityBridgeDesk.slnx')
$deskBuildFiles = @('Verify.ps1','StartDesk.ps1','Publish-Desk.ps1','Package-Desk.ps1','Verify-Package.ps1',
    'Export-Source.ps1','Build-Icon.ps1','DeskLauncher.c','DeskLauncher.rc','DISTRIBUTION_README.md')
$deskSelected = [Collections.Generic.List[string]]::new()
foreach ($deskRelative in $deskRootFiles) { $deskSelected.Add($deskRelative) }
foreach ($deskName in $deskBuildFiles) { $deskSelected.Add('build/' + $deskName) }

function Add-DeskSources([string]$Relative) {
    $deskDirectory = Join-Path $deskRoot $Relative
    if ((Get-Item -LiteralPath $deskDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source directory: $Relative" }
    foreach ($deskItem in Get-ChildItem -LiteralPath $deskDirectory -Force) {
        if ($deskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source item: $($deskItem.Name)" }
        $deskChild = $Relative + '/' + $deskItem.Name
        if ($deskItem.PSIsContainer) {
            if ($deskItem.Name -notin @('bin','obj','.cache','.git','.vs','.idea','.vscode','TestResults','node_modules')) { Add-DeskSources $deskChild }
        } elseif ($deskItem.Extension -in @('.cs','.csproj','.props','.xaml','.manifest','.json','.md','.yml','.yaml','.png','.ico','.txt')) {
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
        if ($deskHash -ne (Get-FileHash -LiteralPath (Join-Path $deskRoot $deskRelative) -Algorithm SHA256).Hash) {
            throw "Source archive hash mismatch: $deskRelative"
        }
    }
}
finally { $deskZip.Dispose() }
Write-Output "Source export verified: $($deskSelected.Count) files."
Write-Output $deskOutput
Write-Output $deskArchive
