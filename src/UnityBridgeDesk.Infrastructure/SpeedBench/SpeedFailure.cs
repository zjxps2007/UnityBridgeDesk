using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed class SpeedMeasurementException(string kind, string message) : IOException(message)
{
    public string Kind { get; } = kind;
}

public static class SpeedFailure
{
    public static string Classify(Exception error, string stage) => error switch
    {
        SpeedMeasurementException measurement => measurement.Kind,
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        JsonException or KeyNotFoundException or FormatException => "invalid-response",
        _ => stage == "preparation" ? "preparation-error" : "execution-error"
    };
    public static string ProcessKind(ProcessOutcome outcome) => outcome switch
    {
        ProcessOutcome.TimedOut => "timeout",
        ProcessOutcome.Cancelled => "cancelled",
        ProcessOutcome.StartFailed => "start-error",
        ProcessOutcome.OutputLimit => "output-limit",
        ProcessOutcome.ProtocolError => "transport-error",
        _ => "cli-error"
    };
    public static string Label(string? kind) => kind switch
    {
        "timeout" => "시간 초과", "response-mismatch" => "응답 불일치", "invalid-response" => "응답 형식 오류",
        "compile-error" => "C# 컴파일 오류", "runtime-error" => "C# 실행 오류", "unsupported-command" => "명령 미지원",
        "cli-error" => "CLI 종료 오류", "start-error" => "실행 시작 실패", "transport-error" => "입출력 오류",
        "output-limit" => "출력 한도 초과", "command-error" => "명령 처리 실패", "preparation-error" => "준비 실패",
        "worker-error" => "측정기 오류", "cleanup-error" => "정리 실패", "cancelled" => "사용자 중단",
        "execution-error" => "실행 중 오류", "none" => "없음", _ => "분류 기록 없음"
    };
    public static JsonElement ReadValue(ProcessResult result, string nonce, int pid, string project)
    {
        var data = ReadData(result);
        try
        {
            if (data.GetProperty("nonce").GetString() != nonce || data.GetProperty("pid").GetInt32() != pid ||
                !InstanceDiscovery.SamePath(data.GetProperty("projectPath").GetString() ?? "", project))
                throw new SpeedMeasurementException("response-mismatch", "요청 식별자·Unity 프로세스·프로젝트가 이번 시행과 다릅니다.");
            return data.GetProperty("value").Clone();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new SpeedMeasurementException("invalid-response", "CLI 응답 형식을 확인하지 못했습니다: " + e.Message); }
    }
    public static JsonElement ReadData(ProcessResult result)
    {
        if (result.Outcome != ProcessOutcome.Exited)
            throw new SpeedMeasurementException(ProcessKind(result.Outcome), $"CLI {result.Outcome} / {result.Error[..Math.Min(1200, result.Error.Length)]}");
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            if (root.GetProperty("success").ValueKind != JsonValueKind.True)
            {
                string message = root.TryGetProperty("message", out var m) ? m.ToString() : "명령이 실패 응답을 반환했습니다.";
                string kind = message.StartsWith("Compile error:", StringComparison.Ordinal) ? "compile-error" :
                    message.StartsWith("Runtime error:", StringComparison.Ordinal) ? "runtime-error" :
                    message.StartsWith("Compiler timed out", StringComparison.Ordinal) ? "timeout" :
                    message.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ? "unsupported-command" : "command-error";
                throw new SpeedMeasurementException(kind, message);
            }
            if (result.ExitCode != 0) throw new SpeedMeasurementException("cli-error", $"성공 응답과 CLI 종료 코드 {result.ExitCode}가 다릅니다.");
            return root.GetProperty("data").Clone();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new SpeedMeasurementException(result.ExitCode == 0 ? "invalid-response" : "cli-error",
            $"CLI 종료 코드 {result.ExitCode} / 응답 형식: {e.Message} / {result.Error[..Math.Min(1200, result.Error.Length)]}"); }
    }
}
