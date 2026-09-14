using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Storage;

public sealed record JournalEvent(int Schema, ExecutionRoute Route, long Sequence, long Timestamp, DateTimeOffset At,
    string Kind, string Text);
public sealed class EventJournal : IDisposable
{
    private readonly StreamWriter writer;
    private readonly ExecutionRoute route;
    private readonly object sync = new();
    private long sequence;
    private long bytes;
    public EventJournal(string file, ExecutionRoute route)
    {
        route.Validate(); this.route = route;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        writer = new(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }
    public void Append(string kind, string text)
    {
        lock (sync)
        {
            string line = JsonSerializer.Serialize(new JournalEvent(1, route, ++sequence, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, kind, Redact(text)));
            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > 128 * 1024 * 1024) throw new IOException("실행 기록 한도 128 MiB에 도달했습니다.");
            writer.WriteLine(line); writer.Flush();
        }
    }
    public static string Redact(string value)
    {
        value = Regex.Replace(value, @"\bsk-[A-Za-z0-9_-]{8,}", "[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return Regex.Replace(value, @"(?i)(authorization\s*[:=]\s*bearer\s+|api[_-]?key\s*[""']?\s*[:=]\s*[""']?)[^\s""',}]+", "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
    public void Dispose() { lock (sync) writer.Dispose(); }
}
