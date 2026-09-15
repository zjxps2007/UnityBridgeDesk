using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Catalog;

public enum DiscoveryKind { Project, Editor, BridgeCli, Connector, AiExecutable, AuthenticationHome }
public sealed record LocalCandidate(DiscoveryKind Kind, string Path, string Label, string Source, string? Version = null);
public sealed record DiscoveryResult(IReadOnlyList<LocalCandidate> Candidates, int UnreadableLocations, bool Limited);
public sealed record DiscoveryLocations(string Home, string Roaming, string Local, string ProgramFiles,
    string Application, string SearchPath, string? CodexHome = null)
{
    public static DiscoveryLocations Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PATH") ?? "", Environment.GetEnvironmentVariable("CODEX_HOME"));
}

// Bounded, local, read-only discovery. Never launches discovered executables or reads authentication contents.
public sealed class LocalDiscovery(DiscoveryLocations locations)
{
    public LocalDiscovery() : this(DiscoveryLocations.Current) { }
    public Task<DiscoveryResult> ScanAsync(CatalogDocument catalog, IEnumerable<string>? extraFolders = null,
        IEnumerable<string>? rememberedPaths = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? configuredPaths = null) =>
        Task.Run(() => Scan(catalog, extraFolders ?? [], rememberedPaths ?? [], configuredPaths, cancellationToken), cancellationToken);

    private DiscoveryResult Scan(CatalogDocument catalog, IEnumerable<string> extraFolders,
        IEnumerable<string> rememberedPaths, IReadOnlyDictionary<string, string>? configuredPaths, CancellationToken ct)
    {
        var found = new Dictionary<string, LocalCandidate>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unreadable = 0, count = 0; bool limited = false;
        void Add(DiscoveryKind kind, string path, string source, string? version = null)
        {
            string label = kind switch {
                DiscoveryKind.Editor => "Unity " + (version ?? "버전 확인 필요"),
                DiscoveryKind.BridgeCli => "Bridge CLI · " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)),
                DiscoveryKind.Connector => "Connector " + version,
                DiscoveryKind.AiExecutable => "Codex", DiscoveryKind.AuthenticationHome => "로그인 정보가 있는 홈",
                _ => System.IO.Path.GetFileName(path) };
            found.TryAdd(kind + "|" + path, new(kind, path, label, source, version));
        }
        void Inspect(string raw, string source, string? configuredKind = null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(raw)) return;
                string path = LocalInspector.NormalizePath(raw);
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                if (File.Exists(path))
                {
                    string name = System.IO.Path.GetFileName(path);
                    if (configuredKind == "codex" && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        Add(DiscoveryKind.AiExecutable, path, source);
                    else if (name.Equals("Unity.exe", StringComparison.OrdinalIgnoreCase))
                        Add(DiscoveryKind.Editor, path, source, EditorVersion(path));
                    else if (name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) Add(DiscoveryKind.AiExecutable, path, source);
                    else if (Regex.IsMatch(name, @"^unity[-]?bridge(?:[-.][\w.-]+)?\.exe$", RegexOptions.IgnoreCase)) Add(DiscoveryKind.BridgeCli, path, source);
                    return;
                }
                if (new[] { "Assets", "Packages", "ProjectSettings" }.All(x => Directory.Exists(System.IO.Path.Combine(path, x))))
                    Add(DiscoveryKind.Project, path, source, ProjectVersion(path));
                string package = System.IO.Path.Combine(path, "package.json");
                if (File.Exists(package))
                {
                    using var json = ReadJson(package);
                    if (json.RootElement.ValueKind == JsonValueKind.Object &&
                        json.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == LocalInspector.ConnectorPackageName &&
                        json.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                        Add(DiscoveryKind.Connector, path, source, version.GetString());
                }
                if (File.Exists(System.IO.Path.Combine(path, "auth.json"))) Add(DiscoveryKind.AuthenticationHome, path, source);
            }
            catch (Exception e) when (ReadFailure(e)) { unreadable++; }
        }
        void Walk(string path, int depth, string source)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return;
                path = LocalInspector.NormalizePath(path);
                if (!visited.Add(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                if (++count > 600) { limited = true; return; }
                Inspect(path, source);
                // A Unity project can be large: inspect its embedded Connector only, never its Library or Assets.
                if (Directory.Exists(System.IO.Path.Combine(path, "ProjectSettings")))
                { Inspect(System.IO.Path.Combine(path, "Packages", LocalInspector.ConnectorPackageName), source); return; }
                var files=Directory.EnumerateFiles(path, "*.exe").Take(81).ToArray();
                if(files.Length>80)limited=true;
                foreach (string file in files.Take(80)) Inspect(file, source);
                if (depth <= 0) return;
                var children=Directory.EnumerateDirectories(path).Take(121).ToArray();
                if(children.Length>120)limited=true;
                foreach (string child in children.Take(120))
                {
                    if (count > 600) { limited = true; break; }
                    if (new[] { ".git", "Library", "Temp", "Logs", "obj", "node_modules" }.Contains(System.IO.Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
                    Walk(child, depth - 1, source);
                }
            }
            catch (Exception e) when (ReadFailure(e)) { unreadable++; }
        }
        // Explicitly selected folders have priority over broad default locations.
        foreach (string path in extraFolders.Take(20)) Walk(path, 3, "지정한 폴더");
        if (configuredPaths is not null)
            foreach (var pair in configuredPaths.Take(100)) Inspect(pair.Value, "이전에 지정한 위치", pair.Key);
        foreach (string path in rememberedPaths.Take(100)) Inspect(path, "이전에 지정한 위치");
        Walk(System.IO.Path.Combine(locations.Local, "UnityBridgeDesk", "releases"), 3, "Desk 버전 보관함");
        foreach (var project in catalog.Projects) Inspect(project.Project.RootPath, "보관함");
        foreach (var artifact in catalog.Artifacts)
        {
            Inspect(artifact.Path, "보관함");
            if (System.IO.Path.GetDirectoryName(artifact.Path) is { } parent) Walk(parent, 1, "등록 파일 주변");
        }
        foreach (string hub in new[] { System.IO.Path.Combine(locations.Roaming, "UnityHub"), System.IO.Path.Combine(locations.Local, "UnityHub") })
        foreach (string file in new[] { "projects-v1.json", "editors-v2.json", "editors.json", "secondaryInstallPath.json" })
        {
            string path = System.IO.Path.Combine(hub, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var json = ReadJson(path);
                foreach (string candidate in JsonPaths(json.RootElement).Take(300))
                {
                    Inspect(candidate, "Unity Hub");
                    if (file.StartsWith("editors") || file == "secondaryInstallPath.json") Walk(candidate, 2, "Unity Hub 설치 위치");
                }
            }
            catch (Exception e) when (ReadFailure(e)) { unreadable++; }
        }
        Walk(System.IO.Path.Combine(locations.ProgramFiles, "Unity", "Hub", "Editor"), 2, "Unity 기본 설치 위치");
        Inspect(System.IO.Path.Combine(locations.ProgramFiles, "Unity", "Editor", "Unity.exe"), "Unity 기본 설치 위치");
        foreach (string folder in locations.SearchPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Take(100))
        foreach (string name in new[] { "unity-bridge.exe", "unity-bridge-windows-amd64.exe", "codex.exe" })
            Inspect(System.IO.Path.Combine(folder.Trim().Trim('"'), name), "시스템 PATH");
        // The desktop app manages native CLI builds outside PATH when Desk is launched from Explorer.
        Walk(System.IO.Path.Combine(locations.Local, "OpenAI", "Codex", "bin"), 1, "Codex 데스크톱 설치 위치");
        Inspect(System.IO.Path.Combine(locations.Home, ".local", "bin", "codex.exe"), "사용자 설치 위치");
        Inspect(System.IO.Path.Combine(locations.Home, ".codex", "bin", "codex.exe"), "사용자 설치 위치");
        if (!string.IsNullOrWhiteSpace(locations.CodexHome)) Inspect(locations.CodexHome, "지정된 Codex 홈");
        Inspect(System.IO.Path.Combine(locations.Home, ".codex"), "기본 로그인 위치");
        // Locate native npm payloads; launcher scripts cannot be passed to the worker as executables.
        foreach (string package in new[] { "codex", "codex-win32-x64", "codex-win32-arm64" })
            Walk(System.IO.Path.Combine(locations.Roaming, "npm", "node_modules", "@openai", package, "vendor"), 3, "npm 설치 위치");
        foreach (string root in new[] { locations.Application, System.IO.Path.Combine(locations.Home, ".unity-bridge"),
            System.IO.Path.Combine(locations.Local, "UnityBridge"), System.IO.Path.Combine(locations.Home, "Downloads"),
            System.IO.Path.Combine(locations.Home, "Unity Projects"), System.IO.Path.Combine(locations.Home, "Documents", "Unity Projects") })
            Walk(root, 2, "기본 위치");
        return new(found.Values.ToArray(), unreadable, limited);
    }

    public static string? ProjectVersion(string path)
    {
        try
        {
            string file = System.IO.Path.Combine(path, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(file) || new FileInfo(file).Length > 65536) return null;
            return File.ReadLines(file).FirstOrDefault(x => x.StartsWith("m_EditorVersion:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
        }
        catch (Exception e) when (ReadFailure(e)) { return null; }
    }
    public static string? EditorVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string value = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
            var match = Regex.Match(value, @"^\d+\.\d+\.\d+[abfp]\d+(?=_|$)");
            return match.Success ? match.Value : null;
        }
        catch (Exception e) when (ReadFailure(e)) { return null; }
    }
    public static LocalCandidate? MatchEditor(IEnumerable<LocalCandidate> candidates, string? projectVersion) =>
        string.IsNullOrWhiteSpace(projectVersion) ? null : candidates.FirstOrDefault(x => x.Kind == DiscoveryKind.Editor && x.Version == projectVersion);
    private static JsonDocument ReadJson(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Metadata too large.");
        using var stream = File.OpenRead(path); return JsonDocument.Parse(stream, new() { MaxDepth = 32 });
    }
    private static IEnumerable<string> JsonPaths(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        { string? value = element.GetString(); if (value is not null && System.IO.Path.IsPathFullyQualified(value)) yield return value; }
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            { if (System.IO.Path.IsPathFullyQualified(property.Name)) yield return property.Name; foreach (string path in JsonPaths(property.Value)) yield return path; }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) foreach (string path in JsonPaths(child)) yield return path;
    }
    private static bool ReadFailure(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException;
}
