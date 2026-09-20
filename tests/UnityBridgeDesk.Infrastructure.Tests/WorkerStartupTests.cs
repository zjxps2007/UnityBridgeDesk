using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class WorkerStartupTests
{
    private static string Worker => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/UnityBridgeDesk.Worker/bin/Release/net10.0-windows/UnityBridgeDesk.Worker.exe"));

    [TestMethod]
    [DataRow("missing-request", "FileNotFoundException")]
    [DataRow("invalid-request", "JsonException")]
    [DataRow("missing-history", "DirectoryNotFoundException")]
    [DataRow("blocked-calibration", "실행기 진단을 시작하지 못했습니다")]
    public async Task InvalidStartupExitsWithFailureInsteadOfAnUnhandledCrash(string scenario, string expected)
    {
        string root = SampleData.TestDirectory();
        string request = Path.Combine(root, "request.json"), result = Path.Combine(root, "result.json");
        string[] arguments = ["--local-trial", request, result];
        if (scenario == "invalid-request") await File.WriteAllTextAsync(request, "{invalid");
        if (scenario == "missing-history") arguments = ["--recover-history", root, Path.Combine(root, "export")];
        if (scenario == "blocked-calibration")
        {
            await File.WriteAllTextAsync(request, "existing file");
            arguments = ["--calibrate", request];
        }
        var reply = await new TimedProcessRunner().RunAsync(new(Guid.NewGuid(), Worker, [..arguments], root),
            TimeSpan.FromSeconds(30));
        Assert.AreEqual(ProcessOutcome.Exited, reply.Outcome, reply.Error);
        Assert.AreEqual(1, reply.ExitCode, reply.Error);
        Assert.Contains(expected, reply.Error);
        Assert.IsFalse(reply.Error.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(File.Exists(result), "A startup failure must not create a successful trial result.");
    }

    [TestMethod]
    public async Task MalformedHandshakeHasAControlledExitAndDoesNotPolluteProtocolOutput()
    {
        var reply = await new TimedProcessRunner().RunAsync(new(Guid.NewGuid(), Worker, [], SampleData.TestDirectory(),
            StandardInput: "{invalid\n"), TimeSpan.FromSeconds(30));
        Assert.AreEqual(ProcessOutcome.Exited, reply.Outcome, reply.Error);
        Assert.AreEqual(1, reply.ExitCode, reply.Error);
        Assert.Contains("JsonException", reply.Error);
        Assert.AreEqual("", reply.Output);
    }

    [TestMethod]
    public async Task ParentPreservesStartupDiagnosticWhenWorkerCannotEmitAFrame()
    {
        var reply = await new WorkerRunner(Worker).RunAsync(new(Guid.NewGuid(), Worker, ["--fixture", "echo"],
            SampleData.TestDirectory(), OutputCodePage: int.MaxValue), TimeSpan.FromSeconds(30));
        Assert.AreEqual(ProcessOutcome.ProtocolError, reply.Outcome, reply.Error);
        Assert.Contains("Worker 실행 실패", reply.Error);
        Assert.Contains("ArgumentOutOfRangeException", reply.Error);
        Assert.IsNull(reply.ProcessId, "Encoding validation must fail before starting a child.");
        Assert.IsNull(reply.ExitCode);
    }
}
