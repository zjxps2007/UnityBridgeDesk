using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record RuntimeFile(string Component, string RelativePath, long Bytes, string Sha256);
public sealed record RuntimeManifest(string Algorithm, string Sha256, RuntimeFile[] Files, string Scope);
public static class ResearchRuntime
{
    public static async Task<RuntimeManifest> Capture(string deskDirectory, string workerDirectory, CancellationToken ct)
    {
        var files = new List<RuntimeFile>();
        foreach (var entry in new[] { ("desk", deskDirectory), ("worker", workerDirectory) })
        {
            string root = Path.GetFullPath(entry.Item2); SpeedFiles.Regular(root);
            if (!Directory.Exists(root)) throw new IOException("실행기 폴더를 찾지 못했습니다: " + entry.Item1);
            // Only deployed code/assets, never settings, arbitrary JSON, credentials or logs.
            async Task Walk(string directory)
            {
                SpeedFiles.Regular(directory);
                foreach (string file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                {
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    bool code = relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        relative.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
                    if (!code && !relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    ct.ThrowIfCancellationRequested(); SpeedFiles.Regular(file);
                    files.Add(new(entry.Item1, relative, new FileInfo(file).Length, await SpeedFiles.Hash(file, ct)));
                }
                foreach (string child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(child);
                    if (name.Equals("Assets", StringComparison.OrdinalIgnoreCase) || name.Equals("runtimes", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetRelativePath(root, directory) != "." || System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures).Any(c => c.Name == name && name.Length > 0))
                        await Walk(child);
                }
            }
            await Walk(root);
        }
        var ordered = files.OrderBy(f => f.Component, StringComparer.Ordinal).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
        return new("sha256", SpeedResearch.Digest(JsonSerializer.Serialize(ordered, SpeedProtocol.Json)), ordered,
            "Desk/Worker deployed code + runtime/config + Assets at run start; excludes user files; not the entire Unity installation or a source-commit attestation");
    }
}
