using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Catalog;

public sealed record BridgeSetupRelease(string Version, string CliSha256, string ConnectorSha256, string Commit)
{
    public Uri CliUrl => new($"https://github.com/zjxps2007/UnityBridge/releases/download/v{Version}/unity-bridge-windows-amd64.exe");
    public Uri SourceUrl => new($"https://codeload.github.com/zjxps2007/UnityBridge/zip/{Commit}");
}
public sealed record PreparedBridgeRelease(BridgeSetupRelease Release, string CliPath, string ConnectorPath);

// Reviewed official assets and immutable source commits. No installer, discovered CLI, or Unity process is run.
public sealed class BridgeEnvironmentSetup
{
    public static IReadOnlyList<BridgeSetupRelease> Recommended { get; } = Array.AsReadOnly(new[] {
        new BridgeSetupRelease("0.2.0", "cd7ceb1b7a2d6bf588481305302fcece02e7fc0b61682beced43b699c95a77ac",
            "a944bd9caa132e5dfbb6bfcc702c35d1000528c9e1a3183d802f4410b613d724", "0313bb5804c5fd8bff1dfdad1a5e1c38d37f93d6"),
        new BridgeSetupRelease("0.2.1", "17bd8d737e12270dd08c1d5cf3dbfaabb805623a7a483437a6e1f7c506e8e305",
            "03c069deb7262ee6a2ec85628eacadb836e1c525c9a7d5d589127c2fcb2a4c0b", "7fb26ae4fc856cf0e176efac23e94506e4749c91") });
    private static readonly HttpClient sharedClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private const long MaxBytes = 128L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly string root;
    private readonly IReadOnlyList<BridgeSetupRelease> definitions;
    private readonly LocalInspector inspector = new();

    public BridgeEnvironmentSetup(string dataRoot, HttpClient? client = null, IReadOnlyList<BridgeSetupRelease>? definitions = null)
    {
        root = Path.Combine(LocalInspector.NormalizePath(dataRoot), "releases");
        this.client = client ?? sharedClient;
        this.definitions = (definitions ?? Recommended).ToArray();
        if (this.definitions.Count is < 1 or > 10 || this.definitions.Select(x => x.Version).Distinct().Count() != this.definitions.Count ||
            this.definitions.Any(x => !Regex.IsMatch(x.Version, @"^\d+\.\d+\.\d+$") ||
                !Regex.IsMatch(x.Commit, "^[a-f0-9]{40}$") || !Regex.IsMatch(x.CliSha256, "^[a-f0-9]{64}$") ||
                !Regex.IsMatch(x.ConnectorSha256, "^[a-f0-9]{64}$"))) throw new ArgumentException("자동 준비 버전 정보가 올바르지 않습니다.");
    }

    public async Task<IReadOnlyList<PreparedBridgeRelease>> PrepareAsync(IEnumerable<LocalCandidate> candidates,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(root); RequireRegular(root);
        FileStream lease;
        try { lease = new(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("다른 환경 준비가 진행 중입니다. 완료한 뒤 다시 눌러 주세요."); }
        using (lease)
        {
            var result = new List<PreparedBridgeRelease>();
            var local = candidates.Where(x => x.Kind is DiscoveryKind.BridgeCli or DiscoveryKind.Connector).Take(200).ToArray();
            var inspected = new Dictionary<string, ArtifactObservation>(StringComparer.OrdinalIgnoreCase);
            foreach (var release in definitions)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"{release.Version} · 보관된 파일 확인 중…");
                string versionRoot = Path.Combine(root, release.Version);
                Directory.CreateDirectory(versionRoot); RequireRegular(versionRoot);
                // Repair uses a new folder; existing references and historical evidence remain intact.
                foreach (string folder in Directory.EnumerateDirectories(versionRoot).Order(StringComparer.Ordinal).Take(100))
                {
                    ct.ThrowIfCancellationRequested(); RequireRegular(folder);
                    var cached = At(release, folder);
                    if (await MatchesAsync(cached).ConfigureAwait(false))
                    { result.Add(cached); progress?.Report($"{release.Version} · 검증된 보관 파일 사용"); break; }
                }
                if (result.Any(x => x.Release.Version == release.Version)) continue;
                string staging = Path.Combine(root, ".preparing-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    var prepared = At(release, staging);
                    foreach (var kind in new[] { DiscoveryKind.BridgeCli, DiscoveryKind.Connector })
                    {
                        string expected = kind == DiscoveryKind.BridgeCli ? release.CliSha256 : release.ConnectorSha256;
                        string? source = null;
                        foreach (var candidate in local.Where(x => x.Kind == kind))
                        {
                            ct.ThrowIfCancellationRequested();
                            string key = kind + "|" + candidate.Path;
                            if (!inspected.TryGetValue(key, out var info))
                                inspected[key] = info = await inspector.InspectArtifactAsync(candidate.Path,
                                    kind == DiscoveryKind.BridgeCli ? ArtifactKind.CliExecutable : ArtifactKind.ConnectorFolder).ConfigureAwait(false);
                            if (info.Status == InspectionStatus.Available && info.Sha256 == expected) { source = candidate.Path; break; }
                        }
                        string name = kind == DiscoveryKind.BridgeCli ? "CLI" : "Connector";
                        progress?.Report($"{release.Version} · {name} " + (source is null ? "공식 파일 받는 중…" : "찾은 파일을 보관함으로 복사 중…"));
                        if (kind == DiscoveryKind.BridgeCli)
                        {
                            if (source is null) await DownloadAsync(release.CliUrl, prepared.CliPath, ct).ConfigureAwait(false);
                            else await CopyFileAsync(source, prepared.CliPath, ct).ConfigureAwait(false);
                        }
                        else if (source is not null) await CopyTreeAsync(source, prepared.ConnectorPath, ct).ConfigureAwait(false);
                        else
                        {
                            string zip = Path.Combine(staging, "source.zip");
                            await DownloadAsync(release.SourceUrl, zip, ct).ConfigureAwait(false);
                            await ExtractConnectorAsync(zip, prepared.ConnectorPath, ct).ConfigureAwait(false);
                            File.Delete(zip);
                        }
                    }
                    progress?.Report($"{release.Version} · CLI·Connector 내용 검증 중…");
                    if (!await MatchesAsync(prepared).ConfigureAwait(false))
                        throw new InvalidDataException($"{release.Version} 파일이 공식 검증값과 일치하지 않습니다. 등록하지 않았어요. 다시 준비하거나 공식 파일을 확인하세요.");
                    await File.WriteAllTextAsync(Path.Combine(staging, "provenance.json"), JsonSerializer.Serialize(new {
                        version = release.Version, cliSha256 = release.CliSha256, connectorSha256 = release.ConnectorSha256,
                        connectorHashScheme = "connector-tree-v1", commit = release.Commit,
                        cliSource = release.CliUrl, connectorSource = release.SourceUrl, verifiedAtUtc = DateTimeOffset.UtcNow
                    }, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    string target = Path.Combine(versionRoot, Guid.NewGuid().ToString("N"));
                    Directory.Move(staging, target);
                    result.Add(At(release, target));
                    progress?.Report($"{release.Version} · 파일 준비 완료");
                }
                finally
                {
                    // Only our unique staging folder is eligible for cleanup.
                    if (Directory.Exists(staging) && Path.GetDirectoryName(Path.GetFullPath(staging)) == root)
                    { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }
            }
            ct.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static PreparedBridgeRelease At(BridgeSetupRelease release, string folder) =>
        new(release, Path.Combine(folder, "unity-bridge.exe"), Path.Combine(folder, "unity-bridge-connector"));
    private async Task<bool> MatchesAsync(PreparedBridgeRelease prepared)
    {
        var cli = await inspector.InspectArtifactAsync(prepared.CliPath, ArtifactKind.CliExecutable).ConfigureAwait(false);
        if (cli.Status != InspectionStatus.Available || cli.Sha256 != prepared.Release.CliSha256) return false;
        var connector = await inspector.InspectArtifactAsync(prepared.ConnectorPath, ArtifactKind.ConnectorFolder).ConfigureAwait(false);
        return connector.Status == InspectionStatus.Available && connector.Sha256 == prepared.Release.ConnectorSha256 &&
            connector.DeclaredVersion == prepared.Release.Version;
    }
    private async Task DownloadAsync(Uri url, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3)); ct = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UnityBridgeDesk/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? "GitHub 다운로드가 제한되었습니다. 잠시 후 다시 준비하거나 공식 파일이 있는 폴더를 추가해 주세요."
                : $"공식 파일을 받지 못했습니다 ({(int)response.StatusCode}). 인터넷 연결을 확인한 뒤 다시 준비해 주세요.");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("다운로드 파일이 허용 크기를 넘었습니다.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await CopyBoundedAsync(input, output, MaxBytes, ct).ConfigureAwait(false);
    }
    private static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        RequireRegular(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await CopyBoundedAsync(input, output, MaxBytes, ct).ConfigureAwait(false);
    }
    private static async Task CopyTreeAsync(string source, string target, CancellationToken ct)
    {
        var pending = new Stack<(string Source, string Target)>(); pending.Push((source, target));
        int count = 0; long total = 0;
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested(); RequireRegular(item.Source); Directory.CreateDirectory(item.Target);
            foreach (var path in Directory.EnumerateFileSystemEntries(item.Source))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 10000) throw new InvalidDataException("Connector 항목 수가 너무 많습니다.");
                if (Path.GetFileName(path) == ".git") continue;
                RequireRegular(path);
                string destination = Path.Combine(item.Target, Path.GetFileName(path));
                if (Directory.Exists(path)) pending.Push((path, destination));
                else
                {
                    total = checked(total + new FileInfo(path).Length);
                    if (total > MaxBytes) throw new InvalidDataException("Connector가 허용 크기를 넘었습니다.");
                    await CopyFileAsync(path, destination, ct).ConfigureAwait(false);
                }
            }
        }
    }
    private static async Task ExtractConnectorAsync(string zip, string target, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 20000) throw new InvalidDataException("원본 압축 파일의 항목 수가 너무 많습니다.");
        var prefixes = archive.Entries.Where(x => x.FullName.EndsWith("/unity-bridge-connector/package.json", StringComparison.Ordinal))
            .Select(x => x.FullName[..^"package.json".Length]).ToArray();
        if (prefixes.Length != 1) throw new InvalidDataException("원본에서 Connector를 찾지 못했습니다.");
        Directory.CreateDirectory(target);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith(prefixes[0], StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            string relative = entry.FullName[prefixes[0].Length..];
            if (relative.Length == 0) continue;
            string[] parts = relative.TrimEnd('/').Split('/');
            if (parts.Any(x => x.Length == 0 || x is "." or ".." || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || x.EndsWith('.') || x.EndsWith(' ')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !names.Add(relative.TrimEnd('/')))
                throw new InvalidDataException("지원하지 않는 Connector 압축 경로입니다.");
            string destination = Path.GetFullPath(Path.Combine(target, Path.Combine(parts)));
            if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Connector 경로가 보관 폴더를 벗어납니다.");
            total = checked(total + entry.Length);
            if (total > MaxBytes || names.Count > 10000) throw new InvalidDataException("Connector가 허용 크기를 넘었습니다.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await CopyBoundedAsync(input, output, entry.Length, ct).ConfigureAwait(false);
        }
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("파일이 허용 크기를 넘었습니다.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
    }
    private static void RequireRegular(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("링크된 경로는 자동 준비에 사용할 수 없습니다.");
    }
}
