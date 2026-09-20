using System.Security.Cryptography;
using System.Text;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class SpeedExec
{
    public const string SourceName = "exec-script.cs", NonceName = "exec-nonce.txt";
    // Identical UTF-8 source for every version and call. Only the input nonce file changes.
    public const string Source = """
        var project = System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        var root = System.IO.Directory.GetParent(project).FullName;
        return new System.Collections.Generic.Dictionary<string, object>
        {
            { "nonce", System.IO.File.ReadAllText(System.IO.Path.Combine(root, "exec-nonce.txt")) },
            { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
            { "projectPath", project },
            { "value", 42 }
        };
        """;
    public static string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source)));
    public static bool UsesExec(SpeedTrial trial) => trial.Experiment == "F04";
    public static string[] Arguments(string root) => ["exec", "--file", Path.Combine(root, SourceName)];
    public static Task WriteSource(string root, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(root, SourceName), Source, new UTF8Encoding(false), ct);
    public static Task WriteNonce(string root, string nonce, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(root, NonceName), nonce, new UTF8Encoding(false), ct);
}
