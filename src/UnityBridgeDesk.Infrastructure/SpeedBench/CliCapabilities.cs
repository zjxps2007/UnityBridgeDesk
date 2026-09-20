using System.Text.RegularExpressions;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

/// <summary>Read help before Unity starts; never probe a measured Connector with a warmup request.</summary>
public sealed record CliCapabilities(bool NoUpdateCheck, string? ExecFileOption)
{
    public static CliCapabilities Parse(string help, string? execHelp = null)
    {
        foreach (string option in new[] { "--json", "--project", "--port", "--instances-dir", "--timeout-ms" })
            if (!Has(help, option)) throw new SpeedMeasurementException("bench-incompatible", $"CLI가 필수 옵션 {option}을 지원하지 않습니다. 같은 조건으로 측정할 수 없습니다.");
        return new(Has(help, "--no-update-check"), execHelp is null ? null :
            Has(execHelp, "--file") ? "--file" : Has(execHelp, "--code-file") ? "--code-file" :
            throw new SpeedMeasurementException("bench-incompatible", "이 CLI는 exec 파일 입력을 지원하지 않습니다."));
    }
    private static bool Has(string help, string option) => Regex.IsMatch(help,
        @"(?<![\w-])" + Regex.Escape(option) + @"(?![\w-])", RegexOptions.CultureInvariant);

    public string[] GlobalArguments(string project, int port, string instances, int timeoutSeconds) =>
        ["--json", ..(NoUpdateCheck ? new[] { "--no-update-check" } : []), "--project", project,
         "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--instances-dir", instances,
         "--timeout-ms", ((long)timeoutSeconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)];

    public static async Task<CliCapabilities> Inspect(string cli, string root, bool exec, CancellationToken ct)
    {
        async Task<string> Help(string[] args, string name)
        {
            var result = await new TimedProcessRunner().RunAsync(new(Guid.NewGuid(), cli, [..args], root),
                TimeSpan.FromSeconds(30), cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            string text = result.Output + "\n" + result.Error;
            await File.WriteAllTextAsync(Path.Combine(root, name), text, ct);
            if (result.Outcome != ProcessOutcome.Exited || result.ExitCode != 0)
                throw new SpeedMeasurementException("bench-incompatible", "CLI 도움말에서 지원 옵션을 확인하지 못했습니다. " + name);
            return text;
        }
        string help = await Help(["--help"], "cli-help.txt");
        string callHelp = await Help(["call", "--help"], "cli-call-help.txt");
        if (!Has(callHelp, "--params")) throw new SpeedMeasurementException("bench-incompatible", "이 CLI는 call --params 입력을 지원하지 않습니다.");
        return Parse(help, exec ? await Help(["exec", "--help"], "cli-exec-help.txt") : null);
    }
}
