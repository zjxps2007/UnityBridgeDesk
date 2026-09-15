param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$deskApp = Join-Path $deskPackage 'app'
$deskManifest = Get-Content -LiteralPath (Join-Path $deskApp 'distribution.json') -Raw | ConvertFrom-Json
$deskVersion = [string]$deskManifest.version
if ($deskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The distribution is missing its release version.' }
foreach ($deskExe in @((Join-Path $deskPackage 'UnityBridge Desk 실행.exe'),(Join-Path $deskApp 'UnityBridgeDesk.exe'),(Join-Path $deskApp 'worker/UnityBridgeDesk.Worker.exe'))) {
    $deskFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($deskExe)
    if (($deskFileVersion.ProductVersion -split '\+')[0] -ne $deskVersion) { throw "Executable version mismatch: $deskExe" }
}
foreach ($deskFile in $deskManifest.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $deskApp $deskFile.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $deskFile.sha256) { throw "Payload mismatch: $($deskFile.path)" }
}
$deskEntries = @(Get-ChildItem -LiteralPath $deskPackage)
if ($deskEntries.Count -ne 3 -or @($deskEntries | Where-Object Extension -eq '.exe').Count -ne 1) { throw 'The package must have one obvious executable, app folder and start guide.' }
# Verify every ZIP entry against the delivered folder without launching the real app.
$deskZip = [IO.Compression.ZipFile]::OpenRead($deskPackage + '.zip')
try {
    $deskPrefix = (Split-Path -Leaf $deskPackage) + '/'
    $deskFileCount = 0
    foreach ($deskEntry in $deskZip.Entries) {
        if (!$deskEntry.Name) { continue }
        $deskName = $deskEntry.FullName.Replace('\','/')
        if (!$deskName.StartsWith($deskPrefix,[StringComparison]::Ordinal)) { throw 'Unexpected archive root' }
        $deskPath = Join-Path $deskPackage $deskName.Substring($deskPrefix.Length)
        $deskStream = $deskEntry.Open()
        try { $deskHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($deskStream)) }
        finally { $deskStream.Dispose() }
        if ($deskHash -ne (Get-FileHash -LiteralPath $deskPath -Algorithm SHA256).Hash) { throw "ZIP mismatch: $deskName" }
        $deskFileCount++
    }
    if ($deskFileCount -ne @(Get-ChildItem -LiteralPath $deskPackage -File -Recurse).Count) { throw 'Archive file count mismatch' }
}
finally { $deskZip.Dispose() }

$deskTest = Join-Path $deskRoot ('.cache/package-build/test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $deskTest '받은 폴더/app'),(Join-Path $deskTest 'other-directory') | Out-Null
@'
#include <windows.h>
#include <shellapi.h>
#include <wchar.h>
static void line(HANDLE file, const WCHAR *value) {
    DWORD written;
    WriteFile(file, value, (DWORD)(wcslen(value)*sizeof(WCHAR)), &written, NULL);
    WriteFile(file, L"\n", sizeof(WCHAR), &written, NULL);
}
int WINAPI wWinMain(HINSTANCE h,HINSTANCE p,PWSTR args,int show) {
    (void)h; (void)p; (void)args; (void)show;
    int count; LPWSTR *values=CommandLineToArgvW(GetCommandLineW(),&count);
    if (!values || count!=5) return 23;
    HANDLE file=CreateFileW(values[1],GENERIC_WRITE,0,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file==INVALID_HANDLE_VALUE) return 24;
    WCHAR cwd[32768],bom=0xfeff; DWORD written;
    WriteFile(file,&bom,sizeof(bom),&written,NULL);
    GetCurrentDirectoryW(32768,cwd); line(file,cwd);
    for(int i=1;i<count;i++) line(file,values[i]);
    CloseHandle(file); LocalFree(values); return 0;
}
'@ | Set-Content -LiteralPath (Join-Path $deskTest 'fixture.c') -Encoding utf8
Push-Location -LiteralPath $deskTest
try {
    & gcc.exe 'fixture.c' -o 'fixture.exe' -municode -mwindows -static -Os -s -lshell32 -Wall -Wextra -Werror
    if ($LASTEXITCODE -ne 0) { throw 'Launcher fixture build failed' }
}
finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $deskTest 'fixture.exe') -Destination (Join-Path $deskTest '받은 폴더/app/UnityBridgeDesk.exe')
Copy-Item -LiteralPath (Join-Path $deskPackage 'UnityBridge Desk 실행.exe') -Destination (Join-Path $deskTest '받은 폴더/UnityBridge Desk 실행.exe')
$deskReport = Join-Path $deskTest 'launch-report.txt'
$deskArguments = '"' + $deskReport + '" "한글 값" "quoted \"value\"" ""'
$deskProcess = Start-Process -FilePath (Join-Path $deskTest '받은 폴더/UnityBridge Desk 실행.exe') -ArgumentList $deskArguments -WorkingDirectory (Join-Path $deskTest 'other-directory') -WindowStyle Hidden -PassThru -Wait
if ($deskProcess.ExitCode -ne 0) { throw 'Launcher failed' }
$deskDeadline = [DateTime]::UtcNow.AddSeconds(5)
while (!(Test-Path -LiteralPath $deskReport) -and [DateTime]::UtcNow -lt $deskDeadline) { Start-Sleep -Milliseconds 100 }
$deskLines = (Get-Content -LiteralPath $deskReport -Raw -Encoding unicode) -split "`n"
if ($deskLines[0] -ne (Join-Path $deskTest '받은 폴더/app').Replace('/','\') -or $deskLines[1] -ne $deskReport -or $deskLines[2] -ne '한글 값' -or $deskLines[3] -ne 'quoted "value"' -or $deskLines[4] -ne '') { throw 'Launcher lost its relative target, working directory, or Unicode/quoted arguments' }
Write-Output "Verified v$deskVersion across launcher, Desktop, Worker and manifest; $($deskManifest.files.Count) payload hashes and $deskFileCount ZIP files. Launcher resolves a moved Unicode folder, sets app working directory, and preserves quoted/empty arguments. No Unity or AI work was started."
