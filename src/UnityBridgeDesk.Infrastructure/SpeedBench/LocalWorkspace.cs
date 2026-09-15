using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record LocalSpeedSettings(string EditorPath = "", SpeedOptions? Options = null, int Palette = 0, string[]? SelectedTags = null);
public sealed record LocalEnvironment(string EditorPath, string EditorVersion, string EditorSha256, string Windows,
    int LogicalProcessors, ulong PhysicalMemoryBytes, string Isolation, string CachePolicy, string NetworkPolicy);
public sealed record LocalTrialRequest(GuestRequest Measurement, string WorkRoot, string EditorPath, string EditorSha256);
public sealed record LocalProcessIdentity(int Pid, DateTimeOffset StartedAt);
public sealed record LocalWorkspaceOwner(Guid TrialId, string Token, LocalProcessIdentity Creator);

// This is an owned workspace, not an OS/security sandbox. User profiles and the system cache stay shared.
public static class LocalWorkspace
{
    public const string Schema = "local-cli-completion-v1-pilot";
    private const string OwnerFile = ".local-owner.json";
    private const string CleanupSuffix = ".cleanup.json";
    public static string Parent(string dataRoot) => Path.Combine(Path.GetFullPath(dataRoot), "speed", "local-work");
    public static async Task<string> Create(string dataRoot, Guid trial, string token, CancellationToken ct)
    {
        string root = Path.Combine(Parent(dataRoot), trial.ToString("N")); SpeedFiles.Regular(root);
        if (Directory.Exists(root)) throw new IOException("이전 실험 폴더는 재사용하지 않습니다.");
        Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess();
        await SpeedFiles.Write(Path.Combine(root, OwnerFile),
            new LocalWorkspaceOwner(trial, token, new(process.Id, process.StartTime.ToUniversalTime())), CancellationToken.None);
        foreach (string folder in new[] { "temp", "upm", "crashes" }) Directory.CreateDirectory(Path.Combine(root, folder));
        return root;
    }
    public static async Task Check(string root, Guid trial, string token)
    {
        SpeedFiles.Regular(root);
        if (Path.GetFileName(Path.GetDirectoryName(root)) != "local-work" || Path.GetFileName(root) != trial.ToString("N"))
            throw new IOException("실험 관리 경로가 아닙니다.");
        var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(Path.Combine(root, OwnerFile));
        if (owner.TrialId != trial || owner.Token != token || token.Length != 32) throw new IOException("실험 폴더 소유권이 다릅니다.");
    }
    public static Dictionary<string, string> EnvironmentFor(string root) => new()
    {
        ["TEMP"] = Path.Combine(root, "temp"), ["TMP"] = Path.Combine(root, "temp"),
        ["UPM_CACHE_ROOT"] = Path.Combine(root, "upm"), ["UPM_NPM_CACHE_PATH"] = Path.Combine(root, "upm", "db"),
        ["UPM_CACHE_PATH"] = Path.Combine(root, "upm", "packages"), ["UPM_GIT_LFS_CACHE_PATH"] = Path.Combine(root, "upm", "git-lfs"),
        ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1"
    };
    public static bool Alive(LocalProcessIdentity identity)
    {
        try { using var process = Process.GetProcessById(identity.Pid); return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - identity.StartedAt.UtcDateTime).TotalMilliseconds) < 2; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    public static async Task Delete(string dataRoot, string root, Guid trial, string token)
    {
        if (!InstanceDiscovery.SamePath(root, Path.Combine(Parent(dataRoot), trial.ToString("N")))) throw new IOException("정리 대상이 관리 폴더를 벗어났습니다.");
        if (!Directory.Exists(root)) return;
        await Check(root, trial, token);
        foreach (string name in new[] { "worker-process.json", "editor-process.json" })
            if (File.Exists(Path.Combine(root, name)) && Alive(await SpeedFiles.Read<LocalProcessIdentity>(Path.Combine(root, name))))
                throw new IOException("시험 프로세스가 아직 실행 중이어서 폴더를 보관했습니다.");
        // Keep an intent record outside the tree, including across a crash between deleting
        // the final inner marker and removing the empty directory.
        string journal = root + CleanupSuffix;
        var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(Path.Combine(root, OwnerFile));
        if (File.Exists(journal) && await SpeedFiles.Read<LocalWorkspaceOwner>(journal) != owner)
            throw new IOException("실험 폴더의 정리 기록과 소유 정보가 다릅니다.");
        await SpeedFiles.Write(journal, owner);
        // Enumerate explicitly before deleting. Never traverse a junction or delete a user-selected project.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                VerifyTree(root);
                DeleteContents(root, preserveOwner: true);
                DeleteFile(Path.Combine(root, OwnerFile));
                ClearReadOnly(root);
                Directory.Delete(root, false);
                DeleteFile(journal); return;
            }
            catch (IOException) when (attempt < 10) { await Task.Delay(300); }
            catch (UnauthorizedAccessException) when (attempt < 10) { await Task.Delay(300); }
        }
    }
    private static void ClearReadOnly(string path)
    {
        SpeedFiles.Regular(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        ClearReadOnly(path); File.Delete(path);
    }
    private static void DeleteContents(string root, bool preserveOwner)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(root))
        {
            SpeedFiles.Regular(item);
            if (preserveOwner && Path.GetFileName(item) == OwnerFile) continue;
            if (Directory.Exists(item))
            {
                DeleteContents(item, preserveOwner: false);
                ClearReadOnly(item); Directory.Delete(item, false);
            }
            else DeleteFile(item);
        }
    }
    private static void VerifyTree(string root)
    {
        var folders = new Queue<string>(); folders.Enqueue(root); int count = 0;
        while (folders.TryDequeue(out string? folder))
        {
            SpeedFiles.Regular(folder);
            foreach (string item in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++count > 200000) throw new IOException("정리 항목 수 제한을 넘었습니다.");
                SpeedFiles.Regular(item);
                if (Directory.Exists(item)) folders.Enqueue(item);
            }
        }
    }
    public static async Task CleanInstance(string root, string? instancesDirectory = null)
    {
        string identityPath = Path.Combine(root, "editor-process.json");
        if (!File.Exists(identityPath)) return;
        var identity = await SpeedFiles.Read<LocalProcessIdentity>(identityPath);
        if (Alive(identity)) throw new IOException("시험 Unity가 아직 실행 중입니다.");
        string instances = instancesDirectory ?? InstanceDiscovery.DefaultDirectory;
        if (!Directory.Exists(instances)) return;
        SpeedFiles.Regular(instances);
        foreach (string file in Directory.EnumerateFiles(instances, "*.json"))
        {
            SpeedFiles.Regular(file); if (new FileInfo(file).Length > 1024 * 1024) continue;
            bool owned;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
                var value = json.RootElement;
                owned = value.TryGetProperty("projectPath", out var project) && project.ValueKind == System.Text.Json.JsonValueKind.String &&
                    InstanceDiscovery.SamePath(project.GetString()!, Path.Combine(root, "project")) &&
                    value.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out int number) && number == identity.Pid;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or ArgumentException) { continue; }
            if (owned) File.Delete(file);
        }
    }
    public static async Task Recover(string dataRoot, IProgress<string>? progress, CancellationToken ct)
    {
        string parent = Parent(dataRoot); SpeedFiles.Regular(parent);
        if (!Directory.Exists(parent)) return;
        foreach (string root in Directory.EnumerateDirectories(parent))
        {
            ct.ThrowIfCancellationRequested();
            SpeedFiles.Regular(root);
            if (!Guid.TryParseExact(Path.GetFileName(root), "N", out Guid trial))
                throw new IOException("실험 관리 경로가 아닌 폴더는 정리하지 않습니다: " + root);
            string marker = Path.Combine(root, OwnerFile), journal = root + CleanupSuffix;
            var owner = File.Exists(marker) ? await SpeedFiles.Read<LocalWorkspaceOwner>(marker, ct) :
                File.Exists(journal) ? await SpeedFiles.Read<LocalWorkspaceOwner>(journal, ct) :
                await RecoverLegacyOwner(dataRoot, root, trial, ct);
            if (owner.TrialId != trial || owner.Token?.Length != 32)
                throw new IOException("실험 폴더 소유권이 다릅니다.");
            if (owner.Creator.Pid != System.Environment.ProcessId && Alive(owner.Creator)) throw new IOException("다른 Desk가 실험 공간을 사용 중입니다.");
            if (!File.Exists(marker)) await SpeedFiles.Write(marker, owner);
            await Check(root, owner.TrialId, owner.Token);
            await CleanInstance(root);
            await Delete(dataRoot, root, owner.TrialId, owner.Token);
            progress?.Report("이전에 남은 시험 폴더를 정리했습니다.");
        }
        // A crash may occur after removing the root but before removing its intent record.
        foreach (string journal in Directory.EnumerateFiles(parent, "*" + CleanupSuffix))
        {
            ct.ThrowIfCancellationRequested(); SpeedFiles.Regular(journal);
            string root = journal[..^CleanupSuffix.Length];
            if (Directory.Exists(root)) continue;
            var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(journal, ct);
            if (Path.GetFileName(root) != owner.TrialId.ToString("N") || owner.Token?.Length != 32)
                throw new IOException("실험 폴더의 정리 기록이 올바르지 않습니다.");
            if (owner.Creator.Pid != System.Environment.ProcessId && Alive(owner.Creator))
                throw new IOException("다른 Desk가 실험 공간을 사용 중입니다.");
            DeleteFile(journal);
        }
    }
    private static async Task<LocalWorkspaceOwner> RecoverLegacyOwner(string dataRoot, string root, Guid trial, CancellationToken ct)
    {
        // v0.3.0 could remove its owner marker before failing on a read-only UPM lock.
        // A GUID-shaped directory alone is not ownership proof: require the matching
        // request AND a recorded failed cleanup, and verify the recorded processes stopped.
        string runs = Path.Combine(Path.GetFullPath(dataRoot), "speed", "local-runs"); SpeedFiles.Regular(runs);
        LocalTrialRequest? match = null; string? evidence = null;
        if (Directory.Exists(runs))
        {
            int count = 0;
            foreach (string runRoot in Directory.EnumerateDirectories(runs))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 10000) throw new IOException("이전 실행 기록 탐색 한도를 넘었습니다.");
                if (!Guid.TryParseExact(Path.GetFileName(runRoot), "N", out Guid runId)) continue;
                string candidate = Path.Combine(runRoot, trial.ToString("N"));
                string requestPath = Path.Combine(candidate, "request.json"), runPath = Path.Combine(runRoot, "run.json");
                if (!File.Exists(requestPath) || !File.Exists(runPath)) continue;
                var request = await SpeedFiles.Read<LocalTrialRequest>(requestPath, ct);
                var run = await SpeedFiles.Read<SpeedRun>(runPath, ct);
                var recorded = run.Results?.Where(r => r.Trial?.Id == trial).ToArray();
                if (request.Measurement is not { } measurement || measurement.Trial?.Id != trial || measurement.RunId != runId ||
                    run.Id != runId || run.Schema != Schema || run.Plan?.Count(t => t == measurement.Trial) != 1 ||
                    recorded is not { Length: 1 } || recorded[0].Trial != measurement.Trial ||
                    recorded[0].Status != "cleanup-failed" || recorded[0].ResetVerified ||
                    !InstanceDiscovery.SamePath(root, request.WorkRoot) || !Guid.TryParseExact(measurement.GuestNonce, "N", out _))
                    throw new IOException("소유 정보가 없는 실험 폴더와 실행 기록이 일치하지 않아 보존했습니다: " + root);
                if (match is not null) throw new IOException("실험 폴더의 실행 기록이 중복되어 보존했습니다: " + root);
                match = request; evidence = candidate;
            }
        }
        if (match is null || evidence is null)
            throw new IOException("실험 폴더의 소유 정보와 복구 가능한 실행 기록을 찾지 못해 보존했습니다. 결과 폴더와 함께 확인해 주세요: " + root);
        string workerPath = Path.Combine(evidence, "worker.json");
        if (!File.Exists(workerPath)) throw new IOException("소유 정보 복구에 필요한 측정기 종료 기록이 없어 폴더를 보존했습니다: " + root);
        var worker = await SpeedFiles.Read<ProcessResult>(workerPath, ct);
        if (worker.ProcessId is not { } pid || worker.StartedAt is not { } started || Alive(new(pid, started)))
            throw new IOException("시험 측정기의 종료를 확인하지 못해 폴더를 보존했습니다: " + root);
        string editorPath = Path.Combine(evidence, "editor-process.json");
        LocalProcessIdentity? editor = File.Exists(editorPath) ? await SpeedFiles.Read<LocalProcessIdentity>(editorPath, ct) : null;
        if (editor is not null && Alive(editor)) throw new IOException("시험 Unity가 아직 실행 중이어서 폴더를 보관했습니다.");
        foreach (string name in new[] { "worker-process.json", "editor-process.json" })
            if (File.Exists(Path.Combine(root, name)) && Alive(await SpeedFiles.Read<LocalProcessIdentity>(Path.Combine(root, name), ct)))
                throw new IOException("시험 프로세스가 아직 실행 중이어서 폴더를 보관했습니다.");
        VerifyTree(root);
        // Restore the editor identity for the existing, strictly matched heartbeat cleanup.
        if (editor is not null) await SpeedFiles.Write(Path.Combine(root, "editor-process.json"), editor);
        using var process = Process.GetCurrentProcess();
        return new(trial, match.Measurement.GuestNonce, new(process.Id, process.StartTime.ToUniversalTime()));
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static (ulong Total, ulong Available, uint Load) Memory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref state)) throw new System.ComponentModel.Win32Exception();
        return (state.Total, state.Available, state.Load);
    }
}
