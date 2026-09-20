using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record GoUnitySelection(string Version = "0.4.1")
{
    public string Tag => "v" + Version.TrimStart('v');
    public void Validate()
    {
        if (!Regex.IsMatch(Version ?? "", @"^v?\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$"))
            throw new ArgumentException("Go unity-cli 버전을 확인하세요. 예: 0.4.1");
    }
}

// The upstream executable and Connector remain unmodified. No global installer or PATH changes.
public sealed class GoUnityRepository(string root, HttpClient? http = null)
{
    public const string Repository = "https://github.com/youngwoocho02/unity-cli";
    public const string PackageName = "com.youngwoocho02.unity-cli-connector";
    private const string Api = "https://api.github.com/repos/youngwoocho02/unity-cli/";
    private const string AssetName = "unity-cli-windows-amd64.exe";
    private static readonly HttpClient shared = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient client = http ?? shared;
    private async Task<HttpResponseMessage> Get(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UnityBridgeDesk-SpeedBench/1.0");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode; response.Dispose();
            throw new HttpRequestException($"Go unity-cli 릴리스를 받지 못했습니다 ({code}). 버전과 인터넷 연결·GitHub 조회 한도를 확인하세요.");
        }
        return response;
    }
    private async Task<JsonDocument> Json(string url, CancellationToken ct)
    {
        using var response = await Get(url, ct); await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); await SpeedFiles.CopyBounded(input, output, 8 * 1024 * 1024, ct);
        return JsonDocument.Parse(output.ToArray());
    }
    private async Task Download(string url, string file, CancellationToken ct)
    {
        using var response = await Get(url, ct); await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(file, FileMode.CreateNew);
        await SpeedFiles.CopyBounded(input, output, 256L * 1024 * 1024, ct);
    }
    public async Task<SpeedRelease> Prepare(GoUnitySelection selection, IProgress<string>? progress, CancellationToken ct, string? editor = null)
    {
        selection.Validate(); string tag = selection.Tag, version = tag[1..];
        SpeedFiles.Regular(root); Directory.CreateDirectory(root);
        progress?.Report($"Go unity-cli {tag} · 릴리스와 소스 커밋 확인");
        using var release = await Json(Api + "releases/tags/" + Uri.EscapeDataString(tag), ct);
        if (release.RootElement.GetProperty("tag_name").GetString() != tag || release.RootElement.GetProperty("draft").GetBoolean())
            throw new IOException("선택한 Go 릴리스와 응답이 다릅니다.");
        var assets = release.RootElement.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (assets.Length != 1) throw new IOException("이 Go 릴리스에는 Windows x64 실행 파일이 없거나 중복되어 있습니다.");
        string url = assets[0].GetProperty("browser_download_url").GetString()!;
        if (url != Repository + "/releases/download/" + tag + "/" + AssetName) throw new IOException("Go CLI 다운로드 주소가 릴리스와 다릅니다.");
        string? digest = assets[0].TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        using var commit = await Json(Api + "commits/" + Uri.EscapeDataString(tag), ct);
        string sha = commit.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(sha, "^[a-f0-9]{40}$")) throw new IOException("Go Connector 소스 커밋을 고정하지 못했습니다.");
        string destination = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(destination);
        string cliDir = Path.Combine(destination, "cli"), connector = Path.Combine(destination, "connector"); Directory.CreateDirectory(cliDir);
        string cli = Path.Combine(cliDir, "unity-cli.exe"), source = Path.Combine(destination, "source.zip");
        progress?.Report($"Go unity-cli {tag} · CLI와 Connector 임시 다운로드");
        await Download(url, cli, ct); string cliHash = await SpeedFiles.Hash(cli, ct);
        if (digest is not null && (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") || !digest[7..].Equals(cliHash, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Go CLI의 게시자 SHA-256과 다운로드 파일이 다릅니다.");
        await Download("https://codeload.github.com/youngwoocho02/unity-cli/zip/" + sha, source, ct);
        using (var archive = ZipFile.OpenRead(source))
        {
            var packages = archive.Entries.Where(e => e.FullName.EndsWith("/unity-connector/package.json", StringComparison.Ordinal)).ToArray();
            if (packages.Length != 1) throw new IOException("Go Connector 패키지 구조가 다릅니다.");
            await SpeedFiles.Extract(source, connector, packages[0].FullName[..^"package.json".Length], ct);
            var license = archive.Entries.SingleOrDefault(e => e.FullName == archive.Entries[0].FullName.Split('/')[0] + "/LICENSE");
            if (license is not null)
            {
                await using var input = license.Open(); await using var output = File.Create(Path.Combine(connector, "UPSTREAM-LICENSE.txt"));
                await SpeedFiles.CopyBounded(input, output, 1024 * 1024, ct);
            }
        }
        File.Delete(source);
        var dependencies = await GoUnityDependencies.Prepare(connector, Path.Combine(destination, "packages"), editor, client, progress, ct);
        var result = new SpeedRelease($"Go unity-cli {tag}", version, sha, cli, connector, cliHash,
            await CliDistribution.TreeHash(connector, ct), url, digest, cliDir, await CliDistribution.TreeHash(cliDir, ct), cliHash,
            GoUnity: new(version, version, tag, dependencies, editor is null ? null : Catalog.LocalDiscovery.EditorVersion(editor)));
        if (!await Valid(result, ct)) throw new IOException("Go CLI 또는 Connector 버전·파일 검증에 실패했습니다.");
        await SpeedFiles.Write(Path.Combine(destination, "release.json"), result, ct); return result;
    }
    public static async Task<bool> Valid(SpeedRelease release, CancellationToken ct)
    {
        try
        {
            if (release.GoUnity is not { } tool || release.OfficialUnity is not null || release.CliDirectory is not { } directory ||
                tool.CliVersion != release.Version || tool.ConnectorVersion != release.Version || tool.ReleaseTag != "v" + release.Version ||
                !Bridge.InstanceDiscovery.SamePath(release.CliPath, Path.Combine(directory, "unity-cli.exe"))) return false;
            SpeedFiles.Regular(release.CliPath); SpeedFiles.Regular(release.ConnectorPath);
            if (await SpeedFiles.Hash(release.CliPath, ct) != release.CliSha256 ||
                await CliDistribution.TreeHash(directory, ct) != release.CliTreeSha256 ||
                await CliDistribution.TreeHash(release.ConnectorPath, ct) != release.ConnectorSha256) return false;
            if (release.PublisherDigest is { } digest && !digest.Equals("sha256:" + release.CliSha256, StringComparison.OrdinalIgnoreCase)) return false;
            if (!await GoUnityDependencies.Valid(tool.Packages, ct)) return false;
            using var package = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(release.ConnectorPath, "package.json"), ct));
            return package.RootElement.GetProperty("name").GetString() == PackageName && package.RootElement.GetProperty("version").GetString() == tool.ConnectorVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException) { return false; }
    }
}

public sealed class GoToolWorkspace : IAsyncDisposable
{
    private readonly string dataRoot; private readonly Guid id; private readonly string token;
    private SpeedRelease? prepared;
    public string Root { get; }
    private GoToolWorkspace(string dataRoot, Guid id, string token, string root)
    { this.dataRoot = dataRoot; this.id = id; this.token = token; Root = root; }
    public static async Task<GoToolWorkspace> Create(string dataRoot, IProgress<string>? progress, CancellationToken ct)
    {
        await LocalWorkspace.Recover(dataRoot, progress, ct, "go-work");
        Guid id = Guid.NewGuid(); string token = Guid.NewGuid().ToString("N");
        return new(dataRoot, id, token, await LocalWorkspace.Create(dataRoot, id, token, ct, "go-work"));
    }
    public async Task<SpeedRelease> Prepare(GoUnitySelection selection, IProgress<string>? progress, CancellationToken ct, HttpClient? http = null, string? editor = null)
    {
        await LocalWorkspace.Check(Root, id, token, "go-work");
        try
        {
            selection.Validate();
            if (prepared?.GoUnity?.ReleaseTag == selection.Tag && prepared.GoUnity.EditorVersion == (editor is null ? null : Catalog.LocalDiscovery.EditorVersion(editor)) && await GoUnityRepository.Valid(prepared, ct)) return prepared;
            return prepared = await new GoUnityRepository(Path.Combine(Root, "artifacts"), http).Prepare(selection, progress, ct, editor);
        }
        catch { await DisposeAsync(); throw; }
    }
    public bool Contains(SpeedRelease release) => release.GoUnity?.Packages is not null && new[] { release.CliPath, release.ConnectorPath }.Concat(release.GoUnity.Packages.Select(p => p.Folder))
        .All(p => Path.GetFullPath(p).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    public async ValueTask DisposeAsync() => await LocalWorkspace.Delete(dataRoot, Root, id, token, "go-work");
}
