using System.Text.Json;
using System.Text.RegularExpressions;
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
        _ => stage == "compatibility" ? "bench-incompatible" : stage == "preparation" ? "preparation-error" : "execution-error"
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
        "instance-unavailable" => "Unity 연결 검색 실패", "bench-incompatible" => "벤치 호환성 오류",
        "output-limit" => "출력 한도 초과", "command-error" => "명령 처리 실패", "preparation-error" => "준비 실패",
        "worker-error" => "측정기 오류", "cleanup-error" => "정리 실패", "cancelled" => "사용자 중단",
        "execution-error" => "실행 중 오류", "none" => "없음", _ => "분류 기록 없음"
    };
    public static JsonElement ReadValue(ProcessResult result, string nonce, int pid, string project)
        => ValidateIdentity(ReadData(result), nonce, pid, project);
    public static JsonElement ValidateIdentity(JsonElement data, string nonce, int pid, string project)
    {
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
        if (result.ExitCode != 0 && IsUnsupportedOption(result.Error))
            throw new SpeedMeasurementException("bench-incompatible", "CLI가 벤치 옵션을 지원하지 않습니다: " + Limit(result.Error));
        // Discovery/transport failures use {ok:false,error:...} on stderr; tool replies
        // use {success:...,data:...} on stdout. Never accept stderr as a success reply.
        if (TryCliFailure(result.Output, out var failure) ||
            ((result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) && TryCliFailure(result.Error, out failure)))
            throw failure!;
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new SpeedMeasurementException(result.ExitCode == 0 ? "invalid-response" : "cli-error",
                result.ExitCode == 0 ? "CLI 표준 출력이 비어 있습니다." :
                $"CLI 종료 코드 {result.ExitCode}" + (string.IsNullOrWhiteSpace(result.Error) ? " · 오류 내용 없음" : " / " + Limit(result.Error)));
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

    private static string Limit(string value) => value[..Math.Min(1200, value.Length)].Trim();

    private static bool TryCliFailure(string text, out SpeedMeasurementException? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind != JsonValueKind.False || !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(error.GetString())) return false;
            string message = Limit(error.GetString()!);
            var port = Regex.Match(message, @"^no (?:active )?instance on port ([0-9]{1,5})$", RegexOptions.CultureInvariant);
            failure = port.Success
                ? new("instance-unavailable", $"CLI가 포트 {port.Groups[1].Value}의 활성 Unity 연결 정보를 찾지 못했습니다. (CLI: {message})")
                : new("cli-error", message);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // Only recognize the exact legacy wrapper and its structured error. The original
    // run/JSON and success/exclusion decisions stay unchanged when viewing old reports.
    private static SpeedMeasurementException? LegacyFailure(string? error)
    {
        if (error is null || !error.StartsWith("CLI 종료 코드 ", StringComparison.Ordinal) ||
            !error.Contains(" / 응답 형식: ", StringComparison.Ordinal)) return null;
        int start = error.IndexOf(" / {", StringComparison.Ordinal);
        return start >= 0 && TryCliFailure(error[(start + 3)..], out var failure) && failure!.Kind == "instance-unavailable"
            ? failure : null;
    }
    private static bool IsUnsupportedOption(string text) => text.Contains("unrecognized arguments: --no-update-check", StringComparison.Ordinal);
    public static string Describe(string error) => IsUnsupportedOption(error) ?
        "벤치가 CLI 미지원 옵션 --no-update-check를 전달했습니다. 버전의 명령 처리 성능 실패가 아닙니다.\n원문: " + error : LegacyFailure(error)?.Message ?? error;
    public static string ReportKind(string kind, string? error) =>
        error is not null && IsUnsupportedOption(error) ? "bench-incompatible" : kind == "cli-error" ? LegacyFailure(error)?.Kind ?? kind : kind;
}
