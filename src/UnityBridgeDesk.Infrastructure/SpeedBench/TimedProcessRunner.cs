using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

// Runs inside the guest for measurements. Hashing and decoding setup precede the clock.
public sealed class TimedProcessRunner : IProcessRunner
{
    public long LastStartedTimestamp { get; private set; }
    public long LastCompletedTimestamp { get; private set; }
    public async Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout,
        Action<ProcessFrame>? observe = null, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(command.FileName) || !Path.IsPathFullyQualified(command.WorkingDirectory) ||
            timeout <= TimeSpan.Zero || command.OutputLimit < 1024) throw new ArgumentException("잘못된 실행 요청입니다.");
        if (command.ExpectedSha256 is { } expected && await SpeedFiles.Hash(command.FileName, cancellationToken) != expected.ToLowerInvariant())
            throw new InvalidDataException("CLI 파일 해시가 변경되었습니다.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(command.OutputCodePage);
        var info = new ProcessStartInfo(command.FileName) { WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding };
        foreach (string arg in command.Arguments) info.ArgumentList.Add(arg);
        if (command.Environment is not null) foreach (var (key, value) in command.Environment) info.Environment[key] = value;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timer.CancelAfter(timeout);
        var output = new StringBuilder(); var error = new StringBuilder(); int total = 0, overflow = 0;
        using var child = new Process { StartInfo = info }; int? pid = null, code = null; DateTimeOffset? started = null;
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = ProcessOutcome.Exited; long first = Stopwatch.GetTimestamp(), last = first;
        LastStartedTimestamp = first;
        Task? drains = null;
        try
        {
            child.Start(); pid = child.Id; started = child.StartTime.ToUniversalTime();
            observe?.Invoke(new(command.CallId, 1, Stopwatch.GetTimestamp(), "started", JsonSerializer.Serialize(new { pid, startedAt = started })));
            async Task Drain(StreamReader reader, StringBuilder target)
            {
                char[] chars = new char[4096]; int n;
                while ((n = await reader.ReadAsync(chars.AsMemory(), timer.Token)) != 0)
                {
                    if (Interlocked.Add(ref total, n) > command.OutputLimit) { Interlocked.Exchange(ref overflow, 1); timer.Cancel(); break; }
                    target.Append(chars, 0, n);
                }
            }
            async Task Input()
            {
                if (command.StandardInput is not null) await child.StandardInput.WriteAsync(command.StandardInput.AsMemory(), timer.Token);
                child.StandardInput.Close();
            }
            drains = Task.WhenAll(Drain(child.StandardOutput, output), Drain(child.StandardError, error), Input(), child.WaitForExitAsync(timer.Token));
            await drains; last = Stopwatch.GetTimestamp(); code = child.ExitCode;
            if (overflow != 0) outcome = ProcessOutcome.OutputLimit;
        }
        catch (OperationCanceledException) { last = Stopwatch.GetTimestamp(); outcome = overflow != 0 ? ProcessOutcome.OutputLimit : cancellationToken.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut; }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        { last = Stopwatch.GetTimestamp(); outcome = pid is null ? ProcessOutcome.StartFailed : ProcessOutcome.ProtocolError; error.Append(e.Message); }
        finally
        {
            if (pid is not null && !child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
            if (drains is not null) { try { await drains; } catch (Exception e) when (e is OperationCanceledException or IOException) { } }
        }
        LastCompletedTimestamp = last;
        return new(outcome, code, output.ToString(), error.ToString(), Stopwatch.GetElapsedTime(first, last).TotalMilliseconds, pid, started);
    }
}
