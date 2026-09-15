using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record HostReply(int ExitCode, string Output, string Error);
public interface IHostCommands
{
    Task<HostReply> Run(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct);
}
public sealed class HostCommands : IHostCommands
{
    public async Task<HostReply> Run(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        using var process = Process.Start(info) ?? throw new IOException("가상머신 관리 도구를 시작하지 못했습니다.");
        var output = new MemoryStream(); var error = new MemoryStream();
        async Task Drain(Stream source, Stream target)
        { try { await SpeedFiles.CopyBounded(source, target, 4 * 1024 * 1024, deadline.Token); } catch { deadline.Cancel(); throw; } }
        Task streams = Task.WhenAll(Drain(process.StandardOutput.BaseStream, output), Drain(process.StandardError.BaseStream, error));
        try { await Task.WhenAll(streams, process.WaitForExitAsync(deadline.Token)); return new(process.ExitCode, Encoding.UTF8.GetString(output.ToArray()), Encoding.UTF8.GetString(error.ToArray())); }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            try { await streams; } catch (Exception e) when (e is IOException or OperationCanceledException) { }
            output.Dispose(); error.Dispose();
        }
    }
}
public sealed record VmChoice(string Id, string Name) { public string Display => Name; }
public sealed class VirtualBoxHost(string manager, IHostCommands? executor = null)
{
    private readonly IHostCommands commands = executor ?? new HostCommands();
    private static readonly string[] pinned = ["cpus", "memory", "ostype", "firmware", "chipset", "vram", "accelerate3d", "clipboard", "draganddrop",
        "nic1", "nic2", "nic3", "nic4", "nic5", "nic6", "nic7", "nic8", "cableconnected1"];
    public static string Discover()
    {
        var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oracle", "VirtualBox", "VBoxManage.exe") };
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(folder)) candidates.Add(Path.Combine(folder, "VBoxManage.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
    public static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            int equals = line.IndexOf('='); if (equals < 1) continue;
            string key = line[..equals].Trim().Trim('"'), value = line[(equals + 1)..].Trim();
            if (value.StartsWith('"') && value.EndsWith('"')) value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (!map.TryAdd(key, value)) throw new IOException("중복된 VM 설정 항목입니다.");
        }
        return map;
    }
    private async Task<string> Run(string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await commands.Run(manager, args, timeout ?? TimeSpan.FromSeconds(60), ct);
        if (response.ExitCode != 0) throw new IOException("VirtualBox " + args[0] + " 실패: " + response.Error[..Math.Min(response.Error.Length, 1000)]);
        return response.Output;
    }
    public Task<string> Version(CancellationToken ct) => Run(["--version"], ct);
    public async Task<VmChoice[]> List(CancellationToken ct)
    {
        var text = await Run(["list", "vms"], ct);
        return Regex.Matches(text, "\"(.+)\" \\{([0-9a-fA-F-]{36})\\}").Select(m => new VmChoice(m.Groups[2].Value, m.Groups[1].Value)).ToArray();
    }
    public async Task<Dictionary<string, string>> Info(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("VM ID가 올바르지 않습니다.");
        return Parse(await Run(["showvminfo", id, "--machinereadable"], ct));
    }
    private static void NoShares(Dictionary<string, string> info)
    {
        if (info.Keys.Any(k => k.StartsWith("SharedFolderName", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("기준 VM의 공유 폴더를 해제한 뒤 다시 준비하세요.");
    }
    public async Task<VmProfile> CloneBaseline(string sourceId, string dataRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var source = await Info(sourceId, ct); NoShares(source);
        if (source.GetValueOrDefault("VMState") != "poweroff") throw new IOException("기준 VM의 Windows를 정상 종료한 뒤 복제해 주세요.");
        string os = source.GetValueOrDefault("ostype", "");
        // showvminfo normally prints the OS description, with the identifier as a fallback.
        if (!os.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
            !(os.EndsWith("_64", StringComparison.OrdinalIgnoreCase) || os.EndsWith("(64-bit)", StringComparison.OrdinalIgnoreCase)) ||
            os.Contains("ARM", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows x64 기준 VM을 선택하세요. 다른 게스트 OS는 지원하지 않습니다.");
        string id = Guid.NewGuid().ToString(), token = Guid.NewGuid().ToString("N"), name = "UnityBridgeBench-" + id[..8];
        string parent = Path.Combine(Path.GetFullPath(dataRoot), "speed", "vms"); Directory.CreateDirectory(parent); SpeedFiles.Regular(parent);
        progress?.Report("시험 전용 VM 복제 중 · 원본 VM은 보존합니다");
        await Run(["clonevm", sourceId, "--mode", "machine", "--options", "keephwuuids,keepallmacs", "--name", name, "--uuid", id, "--basefolder", parent, "--register"], ct, TimeSpan.FromHours(2));
        await Run(["setextradata", id, "UnityBridgeDesk/Owner", token], ct);
        var flags = new List<string> { "modifyvm", id, "--clipboard-mode", "disabled", "--drag-and-drop", "disabled" };
        flags.AddRange(["--nic1", "nat", "--cable-connected1", "on"]);
        for (int i = 2; i <= 8; i++) { flags.Add("--nic" + i); flags.Add("none"); }
        await Run(flags.ToArray(), ct);
        var info = await Info(id, ct); NoShares(info);
        string directory = Path.GetDirectoryName(info["CfgFile"] )!;
        if (!Path.GetFullPath(directory).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("복제 VM 경로 확인 실패.");
        var settings = CaptureSettings(info);
        var disks = XDocument.Load(info["CfgFile"]).Descendants().Where(e => e.Name.LocalName == "HardDisk").Select(e => (string?)e.Attribute("location")).Where(x => x is not null).Cast<string>().ToArray();
        if (disks.Length == 0) throw new IOException("기준 디스크를 확인하지 못했습니다.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string disk in disks)
        {
            string path = Path.GetFullPath(disk, directory);
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("외부 디스크가 연결되어 있습니다.");
            var medium = Parse(await Run(["showmediuminfo", "disk", path, "--machinereadable"], ct));
            if (!medium.GetValueOrDefault("Type", "").StartsWith("normal", StringComparison.OrdinalIgnoreCase)) throw new IOException("일반 모드 디스크만 기준으로 사용할 수 있습니다.");
            progress?.Report("기준 디스크 내용 고정 중 · " + Path.GetFileName(path)); hashes[path] = await SpeedFiles.Hash(path, ct);
        }
        await Run(["snapshot", id, "take", "desk-clean", "--description", "Powered off speed benchmark baseline"], ct, TimeSpan.FromMinutes(10));
        info = await Info(id, ct); string snapshot = info.GetValueOrDefault("CurrentSnapshotUUID") ?? throw new IOException("기준 스냅샷 ID를 확인하지 못했습니다.");
        return new(Path.GetFullPath(manager), id, snapshot, token, directory, settings, hashes, (await Version(ct)).Trim());
    }
    private static Dictionary<string, string> CaptureSettings(Dictionary<string, string> info)
    {
        var result = new Dictionary<string, string>();
        foreach (string key in pinned)
        {
            if (!info.TryGetValue(key, out string? value)) throw new IOException("VM 설정을 확인하지 못했습니다: " + key);
            result[key] = value;
        }
        foreach (var pair in info.Where(p => p.Key.StartsWith("storagecontroller", StringComparison.OrdinalIgnoreCase))) result[pair.Key] = pair.Value;
        return result;
    }
    public async Task VerifyOwnership(VmProfile profile, CancellationToken ct)
    {
        SpeedFiles.Regular(profile.VmDirectory);
        if (!Guid.TryParse(profile.VmId, out _) || !Guid.TryParse(profile.SnapshotId, out _) || profile.OwnershipToken.Length != 32 ||
            !InstancePath(manager, profile.ManagerPath)) throw new IOException("시험 전용 VM 등록 정보가 올바르지 않습니다.");
        string owner = (await Run(["getextradata", profile.VmId, "UnityBridgeDesk/Owner"], ct)).Trim();
        if (owner != "Value: " + profile.OwnershipToken) throw new IOException("앱이 만든 시험 VM이 아닙니다. 원본 VM에는 복원·종료를 수행하지 않습니다.");
        var info = await Info(profile.VmId, ct);
        if (!info.TryGetValue("CfgFile", out var file) || !InstancePath(Path.GetDirectoryName(file)!, profile.VmDirectory)) throw new IOException("시험 VM 위치가 변경되었습니다.");
    }
    private static bool InstancePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public async Task VerifyBaseline(VmProfile profile, CancellationToken ct)
    {
        await VerifyOwnership(profile, ct);
        if (profile.BaselineFiles.Count == 0) throw new IOException("기준 디스크 지문이 없습니다.");
        if (!string.IsNullOrWhiteSpace(await Run(["list", "runningvms"], ct))) throw new IOException("다른 VM이 실행 중입니다. 모든 VM을 종료한 뒤 시험하세요.");
        if ((await Version(ct)).Trim() != profile.ManagerVersion) throw new IOException("VirtualBox 버전이 달라졌습니다. 새 기준을 준비하세요.");
        if ((await Info(profile.VmId, ct)).GetValueOrDefault("VMState") != "poweroff") throw new IOException("시험 VM을 정상 종료한 후 시작하세요.");
        foreach (var (file, hash) in profile.BaselineFiles)
        {
            if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(profile.VmDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("기준 VM 밖의 디스크입니다.");
            if (await SpeedFiles.Hash(file, ct) != hash) throw new IOException("기준 VM 디스크 내용이 변경되었습니다.");
        }
    }
    public async Task Restore(VmProfile profile, CancellationToken ct)
    {
        await VerifyOwnership(profile, ct);
        if ((await Info(profile.VmId, ct)).GetValueOrDefault("VMState") != "poweroff") throw new IOException("VM 전원이 꺼져 있지 않아 복원을 중단했습니다.");
        await Run(["snapshot", profile.VmId, "restore", profile.SnapshotId], ct, TimeSpan.FromMinutes(10));
        var info = await Info(profile.VmId, ct); NoShares(info);
        if (info.GetValueOrDefault("VMState") != "poweroff" || info.GetValueOrDefault("CurrentSnapshotUUID") != profile.SnapshotId)
            throw new IOException("전원이 꺼진 기준 상태로 복원되지 않았습니다.");
        var settings = CaptureSettings(info);
        if (settings.Count != profile.Settings.Count || profile.Settings.Any(p => settings.GetValueOrDefault(p.Key) != p.Value)) throw new IOException("복원된 VM 자원·장치 설정이 달라졌습니다.");
        var disks = XDocument.Load(info["CfgFile"]).Descendants().Where(e => e.Name.LocalName == "HardDisk").ToArray();
        if (disks.Length == 0) throw new IOException("복원된 디스크를 확인할 수 없습니다.");
        foreach (var disk in disks)
        {
            string path = Path.GetFullPath((string?)disk.Attribute("location") ?? throw new IOException("디스크 위치 미제공."), profile.VmDirectory);
            if (!path.StartsWith(profile.VmDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("복원에서 제외되는 외부 디스크입니다.");
            if (disk.Parent?.Name.LocalName != "HardDisk" && !profile.BaselineFiles.ContainsKey(path)) throw new IOException("기준에 없던 디스크입니다.");
            var medium = Parse(await Run(["showmediuminfo", "disk", path, "--machinereadable"], ct));
            if (!medium.GetValueOrDefault("Type", "").StartsWith("normal", StringComparison.OrdinalIgnoreCase)) throw new IOException("복원 가능한 일반 디스크가 아닙니다.");
        }
    }
    public async Task Start(VmProfile profile, CancellationToken ct)
    { await VerifyOwnership(profile, ct); await Run(["startvm", profile.VmId, "--type", "headless"], ct); }
    public async Task DisconnectForMeasurement(VmProfile profile, CancellationToken ct)
    {
        await VerifyOwnership(profile, ct); await Run(["controlvm", profile.VmId, "setlinkstate1", "off"], ct);
        var info = await Info(profile.VmId, ct);
        if (info.GetValueOrDefault("cableconnected1") != "off" || Enumerable.Range(2, 7).Any(i => info.GetValueOrDefault("nic" + i) != "none"))
            throw new IOException("측정 전 게스트 네트워크 차단을 확인하지 못했습니다.");
    }
    public async Task Stop(VmProfile profile, CancellationToken ct)
    {
        await VerifyOwnership(profile, ct);
        var state = (await Info(profile.VmId, ct)).GetValueOrDefault("VMState");
        if (state == "poweroff") return;
        if (state is not ("running" or "paused" or "stuck")) throw new IOException("시험 VM 종료 상태를 확인해야 합니다: " + state);
        await Run(["controlvm", profile.VmId, "poweroff"], ct);
        if ((await Info(profile.VmId, ct)).GetValueOrDefault("VMState") != "poweroff") throw new IOException("VM 종료를 확인하지 못했습니다.");
    }
    private string[] Auth(string user, string passwordFile) => ["--username", user, "--passwordfile", passwordFile];
    public Task<string> Guest(VmProfile profile, string user, string passwordFile, string exe, string[] args, CancellationToken ct, TimeSpan timeout) =>
        Run(["guestcontrol", profile.VmId, "run", ..Auth(user, passwordFile), "--exe", exe, "--timeout", ((long)timeout.TotalMilliseconds).ToString(),
            "--wait-stdout", "--wait-stderr", "--", ..args], ct, timeout + TimeSpan.FromSeconds(20));
    public async Task WaitGuest(VmProfile profile, string user, string passwordFile, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMinutes(5))
        {
            ct.ThrowIfCancellationRequested();
            try { await Guest(profile, user, passwordFile, @"C:\Windows\System32\cmd.exe", ["/d", "/c", "ver"], ct, TimeSpan.FromSeconds(10)); return; }
            catch (IOException) { }
            await Task.Delay(2000, ct);
        }
        throw new IOException("게스트 연결 시간 초과. Guest Additions와 Windows 사용자·암호를 확인하세요.");
    }
    public Task<string> CopyTo(VmProfile profile, string user, string passwordFile, string file, string target, CancellationToken ct) =>
        Run(["guestcontrol", profile.VmId, "copyto", ..Auth(user, passwordFile), "--target-directory", target, file], ct, TimeSpan.FromMinutes(10));
    public Task<string> CopyFrom(VmProfile profile, string user, string passwordFile, string file, string target, CancellationToken ct) =>
        Run(["guestcontrol", profile.VmId, "copyfrom", ..Auth(user, passwordFile), file, target], ct, TimeSpan.FromMinutes(10));
    public Task<string> PowerShell(VmProfile profile, string user, string passwordFile, string code, CancellationToken ct) =>
        Guest(profile, user, passwordFile, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(code))], ct, TimeSpan.FromMinutes(10));
}
