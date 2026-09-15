using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public static string Parent(string dataRoot) => Path.Combine(Path.GetFullPath(dataRoot), "speed", "local-work");
    public static async Task<string> Create(string dataRoot, Guid trial, string token, CancellationToken ct)
    {
        string root = Path.Combine(Parent(dataRoot), trial.ToString("N")); SpeedFiles.Regular(root);
        if (Directory.Exists(root)) throw new IOException("이전 실험 폴더는 재사용하지 않습니다.");
        Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess();
        await SpeedFiles.Write(Path.Combine(root, ".local-owner.json"),
            new LocalWorkspaceOwner(trial, token, new(process.Id, process.StartTime.ToUniversalTime())), CancellationToken.None);
        foreach (string folder in new[] { "temp", "upm", "crashes" }) Directory.CreateDirectory(Path.Combine(root, folder));
        return root;
    }
    public static async Task Check(string root, Guid trial, string token)
    {
        SpeedFiles.Regular(root);
        if (Path.GetFileName(Path.GetDirectoryName(root)) != "local-work" || Path.GetFileName(root) != trial.ToString("N"))
            throw new IOException("실험 관리 경로가 아닙니다.");
        var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(Path.Combine(root, ".local-owner.json"));
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
        // Enumerate explicitly before deleting. Never traverse a junction or delete a user-selected project.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                VerifyTree(root);
                Directory.Delete(root, true); return;
            }
            catch (IOException) when (attempt < 10) { await Task.Delay(300); }
            catch (UnauthorizedAccessException) when (attempt < 10) { await Task.Delay(300); }
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
            var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(Path.Combine(root, ".local-owner.json"), ct);
            if (owner.Creator.Pid != System.Environment.ProcessId && Alive(owner.Creator)) throw new IOException("다른 Desk가 실험 공간을 사용 중입니다.");
            await Check(root, owner.TrialId, owner.Token);
            await CleanInstance(root);
            await Delete(dataRoot, root, owner.TrialId, owner.Token);
            progress?.Report("이전에 남은 시험 폴더를 정리했습니다.");
        }
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
