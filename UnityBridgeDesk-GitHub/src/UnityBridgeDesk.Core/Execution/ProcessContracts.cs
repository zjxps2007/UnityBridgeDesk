using System.Collections.Immutable;

namespace UnityBridgeDesk.Core.Execution;

public sealed record ProcessCommand(Guid CallId, string FileName, ImmutableArray<string> Arguments,
    string WorkingDirectory, string? StandardInput = null, Dictionary<string, string>? Environment = null,
    string? ExpectedSha256 = null, int OutputLimit = 16 * 1024 * 1024, int OutputCodePage = 65001);
public sealed record ProcessFrame(Guid CallId, long Sequence, long Timestamp, string Kind, string Text);
public enum ProcessOutcome { Exited, Cancelled, TimedOut, OutputLimit, ProtocolError, StartFailed }
public sealed record ProcessResult(ProcessOutcome Outcome, int? ExitCode, string Output, string Error,
    double ElapsedMilliseconds, int? ProcessId, DateTimeOffset? StartedAt);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout,
        Action<ProcessFrame>? observe = null, CancellationToken cancellationToken = default);
}
