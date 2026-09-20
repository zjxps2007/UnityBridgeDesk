using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed class ReleaseRepository(string dataRoot, HttpClient? http = null)
{
    private static readonly HttpClient shared = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient client = http ?? shared;
    private const string Api = "https://api.github.com/repos/zjxps2007/UnityBridge/";
    private string Root => Path.Combine(dataRoot, "speed", "releases");
    private async Task<HttpResponseMessage> Get(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UnityBridgeDesk-SpeedBench/1.0");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode; response.Dispose();
            throw new HttpRequestException(code is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? "GitHub 조회 한도에 도달했습니다. 보관된 목록을 사용하거나 잠시 후 다시 조회하세요."
                : "공식 릴리스를 받지 못했습니다: " + (int)code);
        }
        return response;
    }
    private async Task<JsonDocument> Json(string url, CancellationToken ct)
    {
        using var response = await Get(url, ct); await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream(); await SpeedFiles.CopyBounded(stream, memory, 8 * 1024 * 1024, ct);
        return JsonDocument.Parse(memory.ToArray());
    }
    public async Task<ReleaseChoice[]> List(CancellationToken ct)
    {
        var choices = new List<ReleaseChoice>();
        for (int page = 1; page <= 5; page++)
        {
            using var json = await Json(Api + $"releases?per_page=100&page={page}", ct);
            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean()) continue;
                string tag = release.GetProperty("tag_name").GetString()!, version = tag.TrimStart('v');
                if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$")) continue;
                JsonElement? asset = null;
                foreach (string name in CliDistribution.AssetNames)
                {
                    var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
                    if (matches.Length > 1) throw new IOException("중복된 Windows 배포 파일입니다: " + tag);
                    if (matches.Length == 1) { asset = matches[0]; break; }
                }
                string? url = asset?.GetProperty("browser_download_url").GetString();
                string? digest = asset is { } chosen && chosen.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (!CliDistribution.OfficialAsset(tag, url)) url = null;
                choices.Add(new(release.GetProperty("id").GetInt64(), tag, version, release.GetProperty("name").GetString() ?? tag,
                    release.GetProperty("prerelease").GetBoolean(), url, digest, release.GetProperty("html_url").GetString() ?? "https://github.com/zjxps2007/UnityBridge/releases"));
            }
            if (json.RootElement.GetArrayLength() < 100) break;
        }
        var result = choices.ToArray(); await SpeedFiles.Write(Path.Combine(Root, "release-list.json"), result, ct); return result;
    }
    public async Task<ReleaseChoice[]> CachedList(CancellationToken ct = default) =>
        File.Exists(Path.Combine(Root, "release-list.json")) ? await SpeedFiles.Read<ReleaseChoice[]>(Path.Combine(Root, "release-list.json"), ct) : [];
    public async Task<SpeedRelease> Prepare(ReleaseChoice choice, IProgress<string>? progress, CancellationToken ct)
    {
        if (!CliDistribution.OfficialAsset(choice.Tag, choice.CliUrl) ||
            !Regex.IsMatch(choice.Version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$")) throw new IOException("이 릴리스에는 지원하는 Windows CLI가 없습니다.");
        Directory.CreateDirectory(Root); SpeedFiles.Regular(Root);
        using var lease = new FileStream(Path.Combine(Root, "prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string versionRoot = Path.Combine(Root, choice.Version); Directory.CreateDirectory(versionRoot);
        foreach (var folder in Directory.EnumerateDirectories(versionRoot).Take(100))
        {
            string manifest = Path.Combine(folder, "release.json"); if (!File.Exists(manifest)) continue;
            SpeedRelease candidate;
            try { candidate = await SpeedFiles.Read<SpeedRelease>(manifest, ct); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { continue; }
            if (candidate.Tag == choice.Tag && candidate.CliUrl == choice.CliUrl && candidate.PublisherDigest == choice.PublisherDigest && await Valid(candidate, ct))
            { progress?.Report(choice.Tag + " · 검증된 보관 파일 사용"); return candidate; }
        }
        progress?.Report(choice.Tag + " · 소스 커밋 확인");
        using var commit = await Json(Api + "commits/" + Uri.EscapeDataString(choice.Tag), ct);
        string sha = commit.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(sha, "^[a-f0-9]{40}$")) throw new IOException("소스 커밋을 고정할 수 없습니다.");
        string destination = Path.Combine(versionRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(destination);
        bool bundle = CliDistribution.IsBundle(choice.CliUrl);
        string cliDirectory = Path.Combine(destination, "cli"), cli = Path.Combine(cliDirectory, "unity-bridge.exe"),
            assetPath = Path.Combine(destination, bundle ? "download.zip" : "download.exe"),
            source = Path.Combine(destination, "source.zip"), connector = Path.Combine(destination, "connector");
        progress?.Report(choice.Tag + " · CLI 패키지·Connector 받는 중 (" + CliDistribution.Description(choice.CliUrl) + ")");
        await Download(choice.CliUrl!, assetPath, ct);
        string assetHash = await SpeedFiles.Hash(assetPath, ct);
        if (choice.PublisherDigest is { } digest && (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ||
            !digest[7..].Equals(assetHash, StringComparison.OrdinalIgnoreCase))) throw new IOException("게시자가 제공한 배포 파일 SHA-256과 다릅니다.");
        if (bundle) await CliDistribution.ExtractBundle(assetPath, cliDirectory, ct);
        else { Directory.CreateDirectory(cliDirectory); File.Copy(assetPath, cli); }
        CliDistribution.ValidateLayout(cliDirectory, bundle);
        string cliTreeHash = await CliDistribution.TreeHash(cliDirectory, ct);
        await Download("https://codeload.github.com/zjxps2007/UnityBridge/zip/" + sha, source, ct);
        using (var zip = ZipFile.OpenRead(source))
        {
            var packagePaths = zip.Entries.Where(e => e.FullName.EndsWith("/unity-bridge-connector/package.json", StringComparison.Ordinal)).ToArray();
            if (packagePaths.Length != 1) throw new IOException("이 릴리스의 Connector 소스 구조를 확인해야 합니다.");
            string prefix = packagePaths[0].FullName[..^"package.json".Length];
            await SpeedFiles.Extract(source, connector, prefix, ct);
        }
        var inspector = new LocalInspector(); var exe = await inspector.InspectArtifactAsync(cli, ArtifactKind.CliExecutable);
        var package = await inspector.InspectArtifactAsync(connector, ArtifactKind.ConnectorFolder);
        if (exe.Status != InspectionStatus.Available || package.Status != InspectionStatus.Available || package.DeclaredVersion != choice.Version)
            throw new IOException("CLI/Connector 내용 또는 패키지 버전이 릴리스와 맞지 않습니다.");
        var result = new SpeedRelease(choice.Tag, choice.Version, sha, cli, connector, exe.Sha256!, package.Sha256!, choice.CliUrl!, choice.PublisherDigest,
            cliDirectory, cliTreeHash, assetHash);
        await SpeedFiles.Write(Path.Combine(destination, "release.json"), result, ct); File.Delete(source); File.Delete(assetPath);
        if (CliDistribution.CompatibilityNote(result) is { } note) progress?.Report(note);
        return result;
    }
    public static async Task<bool> Valid(SpeedRelease release, CancellationToken ct)
    {
        if (release.GoUnity is not null) return release.OfficialUnity is null && await GoUnityRepository.Valid(release, ct);
        if (release.OfficialUnity is not null) return await OfficialUnityRepository.Valid(release, ct);
        try
        {
            if (!File.Exists(release.CliPath) || !Directory.Exists(release.ConnectorPath)) return false;
            if (await SpeedFiles.Hash(release.CliPath, ct) != release.CliSha256) return false;
            if (release.CliDirectory is { } directory)
            {
                if (!Path.GetFullPath(release.CliPath).Equals(Path.GetFullPath(Path.Combine(directory, "unity-bridge.exe")), StringComparison.OrdinalIgnoreCase)) return false;
                CliDistribution.ValidateLayout(directory, CliDistribution.IsBundle(release.CliUrl));
                if (release.CliTreeSha256 is null || await CliDistribution.TreeHash(directory, ct) != release.CliTreeSha256) return false;
            }
            else if (CliDistribution.IsBundle(release.CliUrl) || release.CliTreeSha256 is not null) return false;
            string? artifactHash = release.AssetSha256 ?? (CliDistribution.IsBundle(release.CliUrl) ? null : release.CliSha256);
            if (release.PublisherDigest is { } digest && !string.Equals(digest, "sha256:" + artifactHash, StringComparison.OrdinalIgnoreCase)) return false;
            var connector = await new LocalInspector().InspectArtifactAsync(release.ConnectorPath, ArtifactKind.ConnectorFolder);
            return connector.Status == InspectionStatus.Available && connector.Sha256 == release.ConnectorSha256 && connector.DeclaredVersion == release.Version;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private async Task Download(string url, string target, CancellationToken ct)
    {
        using var response = await Get(url, ct); await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(target, FileMode.CreateNew);
        await SpeedFiles.CopyBounded(stream, file, 256L * 1024 * 1024, ct);
    }
}
