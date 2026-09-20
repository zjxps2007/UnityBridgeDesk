using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;

namespace UnityBridgeDesk.Infrastructure.Execution;

public sealed class WorkerRunner(string workerPath) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout,
        Action<ProcessFrame>? observe = null, CancellationToken cancellationToken = default)
    {
        if (command.CallId == Guid.Empty || !Path.IsPathFullyQualified(command.FileName) ||
            !Path.IsPathFullyQualified(command.WorkingDirectory) || command.OutputLimit < 1024 || timeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid process request.");
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var output = new StringBuilder(); var error = new StringBuilder();
        int? pid = null, exit = null; DateTimeOffset? started = null;
        ProcessOutcome outcome = ProcessOutcome.Exited;
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
        using var job = new WindowsJob();
        using var worker = new Process { StartInfo = new ProcessStartInfo(workerPath)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
          RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
          StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
          WorkingDirectory = command.WorkingDirectory } };
        Task<string>? diagnostic = null;
        try
        {
            if (command.ExpectedSha256 is not null)
            {
                await using var file = File.OpenRead(command.FileName);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, linked.Token));
                if (!hash.Equals(command.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Executable hash changed.");
            }
            worker.Start();
            diagnostic = worker.StandardError.ReadToEndAsync(linked.Token);
            try { job.Assign(worker); }
            catch { worker.Kill(); throw; } // Worker has not received a command and cannot spawn yet.
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), linked.Token);
            worker.StandardInput.Close();
            long sequence = 0;
            while (await worker.StandardOutput.ReadLineAsync(linked.Token) is { } line)
            {
                if (line.Length > 32768) throw new InvalidDataException("Oversized worker frame.");
                var frame = JsonSerializer.Deserialize<ProcessFrame>(line) ?? throw new InvalidDataException("Empty frame.");
                if (frame.CallId != command.CallId || frame.Sequence != ++sequence || exit is not null)
                    throw new InvalidDataException("Foreign, reordered or late worker frame.");
                switch (frame.Kind)
                {
                    case "started":
                        using (var data = JsonDocument.Parse(frame.Text))
                        { pid = data.RootElement.GetProperty("pid").GetInt32(); started = data.RootElement.GetProperty("startedAt").GetDateTimeOffset(); }
                        break;
                    case "stdout": output.Append(frame.Text); break;
                    case "stderr": error.Append(frame.Text); break;
                    case "exit": exit = int.Parse(frame.Text, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "error": throw new InvalidDataException(frame.Text);
                    default: throw new InvalidDataException("Unknown worker frame.");
                }
                observe?.Invoke(frame);
                if (output.Length + error.Length > command.OutputLimit)
                { outcome = ProcessOutcome.OutputLimit; break; }
            }
            if (outcome == ProcessOutcome.Exited)
            {
                await worker.WaitForExitAsync(linked.Token);
                if (exit is null || worker.ExitCode != 0) outcome = ProcessOutcome.ProtocolError;
            }
        }
        catch (OperationCanceledException) { outcome = cancellationToken.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException or FormatException or InvalidOperationException)
        { outcome = pid is null ? ProcessOutcome.StartFailed : ProcessOutcome.ProtocolError; error.Append(e.Message); }
        finally
        {
            try { await job.StopAndWaitAsync(); } // Verify descendants have stopped before the caller removes their files.
            finally { job.Dispose(); }
            try { if (worker.Id != 0) await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException) { }
            // Startup failures may happen before a framed message can be emitted.
            // Preserve stderr so a missing exit frame still carries its actual cause.
            if (diagnostic is not null) { try { error.Append(await diagnostic); } catch (OperationCanceledException) { } }
        }
        if (cancellationToken.IsCancellationRequested) outcome = ProcessOutcome.Cancelled;
        return new(outcome, exit, output.ToString(), error.ToString(), clock.Elapsed.TotalMilliseconds, pid, started);
    }
}
