using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Infrastructure.Ai;

public sealed record AiOptions(string Executable, string Model, string Reasoning, string AuthenticationHome,
    int TimeoutSeconds, int MaxToolCalls, long? ReportedTokenLimit, bool AllowWorkspaceAndNetwork)
{
    public void Validate()
    {
        if (!File.Exists(Executable) || !Path.IsPathFullyQualified(Executable) || string.IsNullOrWhiteSpace(Model) ||
            Reasoning is not ("low" or "medium" or "high" or "xhigh" or "max" or "ultra") ||
            TimeoutSeconds < 1 || MaxToolCalls < 1 || ReportedTokenLimit <= 0 || !AllowWorkspaceAndNetwork)
            throw new InvalidOperationException("AI 실행 파일·모델·추론·시간·호출 한도·작업 폴더 및 네트워크 권한을 설정하세요.");
        if (!Path.IsPathFullyQualified(AuthenticationHome) || !File.Exists(Path.Combine(AuthenticationHome, "auth.json")))
            throw new InvalidOperationException("파일 인증이 설정된 전용 Codex 홈을 지정하세요. 인증 내용은 기록하지 않습니다.");
    }
}
public sealed record AiResult(bool Completed, string? ThreadId, string Message, long? InputTokens, long? CachedTokens,
    long? OutputTokens, int ToolCalls, string? Failure, ProcessResult Process);
public interface IAiSession : IAsyncDisposable
{
    Task<AiResult> SendAsync(string instruction, Action<string, string> observe, CancellationToken ct);
}
public interface IAiRunner
{
    bool IsSynthetic => false;
    Task<IAiSession> CreateAsync(AiOptions options, BridgeTarget target, string privateRoot, CancellationToken ct);
}
public sealed class CodexRunner(IProcessRunner processes) : IAiRunner
{
    public async Task<IAiSession> CreateAsync(AiOptions options, BridgeTarget target, string privateRoot, CancellationToken ct)
    {
        options.Validate();
        string home = Path.Combine(privateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        string token = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(home, ".desk-owner"), token, ct);
        // Only authentication is seeded. No config, sessions, memory, skills or earlier outputs.
        await using (var source = new FileStream(Path.Combine(options.AuthenticationHome, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var dest = new FileStream(Path.Combine(home, "auth.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await source.CopyToAsync(dest, ct);
        return new Session(processes, options, target, home, privateRoot, token, ProjectFiles.HashFile(Path.Combine(home,"auth.json")));
    }
    private sealed class Session(IProcessRunner runner, AiOptions options, BridgeTarget target,
        string home, string privateRoot, string token, string originalAuthHash) : IAiSession
    {
        private string? thread;
        private bool unusable;
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<AiResult> SendAsync(string instruction, Action<string, string> observe, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (unusable) throw new InvalidOperationException("종료·실패한 세션은 새 작업으로 시작하세요.");
                if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("AI 지시가 비어 있습니다.");
                var args = new List<string> { "-a", "never", "-C", target.ProjectPath, "-s", "workspace-write", "exec",
                    "--json", "--ignore-user-config", "--skip-git-repo-check", "-m", options.Model,
                    "-c", "model_reasoning_effort=" + JsonSerializer.Serialize(options.Reasoning),
                    "-c", "sandbox_workspace_write.network_access=true" };
                if (thread is not null) { args.Add("resume"); args.Add(thread); }
                args.Add("-");
                string system = $"Unity 작업 대상은 {target.ProjectPath} 입니다. Unity 조작과 조회는 반드시 다음 등록 CLI를 사용하세요: {target.CliExecutable}. 모든 호출에 --project {JsonSerializer.Serialize(target.ProjectPath)} --json --no-update-check 를 지정하세요. 다른 Unity 자동화나 Editor UI를 사용하지 마세요. 코드 파일 작성은 허용합니다. 다른 폴더의 대화·메모리·인증 파일을 읽지 마세요. 작업과 무관한 변경, 하위 에이전트, 외부 발송을 하지 마세요. 결과와 변경 파일을 보고하세요.\n\n";
                observe("instruction", instruction);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var events = new CodexEvents(thread, options.MaxToolCalls, options.ReportedTokenLimit, observe, () => budget.Cancel());
                var environment = new Dictionary<string, string> { ["CODEX_HOME"] = home,
                    ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ["OPENAI_API_KEY"] = "", ["CODEX_API_KEY"] = "", ["OPENAI_BASE_URL"] = "" };
                var process = await runner.RunAsync(new(Guid.NewGuid(), options.Executable, [..args], target.ProjectPath,
                    system + instruction, environment, ProjectFiles.HashFile(options.Executable)), TimeSpan.FromSeconds(options.TimeoutSeconds),
                    frame => { if (frame.Kind == "stdout") events.Feed(frame.Text); else if (frame.Kind == "stderr") observe("diagnostic", EventJournal.Redact(frame.Text)); }, budget.Token);
                events.Finish();
                thread ??= events.Thread;
                bool completed = events.Completed && events.Failure is null && process.Outcome == ProcessOutcome.Exited && process.ExitCode == 0;
                if (!completed) unusable = true;
                return new(completed, thread, events.Message, events.InputTokens, events.CachedTokens, events.OutputTokens,
                    events.ToolCalls, events.Failure ?? (completed ? null : ct.IsCancellationRequested ? "Cancelled" : process.Outcome.ToString()), process);
            }
            finally { gate.Release(); }
        }
        public ValueTask DisposeAsync()
        {
            unusable = true;
            string activeAuth=Path.Combine(home,"auth.json"), originalAuth=Path.Combine(options.AuthenticationHome,"auth.json");
            // Preserve provider token rotation without overwriting concurrent login changes.
            if(File.Exists(activeAuth) && ProjectFiles.HashFile(activeAuth)!=originalAuthHash)
            {
                if(!File.Exists(originalAuth)||ProjectFiles.HashFile(originalAuth)!=originalAuthHash)
                    throw new IOException("인증이 다른 작업에서 바뀌었습니다. 별도 인증 홈을 확인하세요.");
                string temp=originalAuth+".desk-"+Guid.NewGuid().ToString("N");
                File.Copy(activeAuth,temp,false);File.Move(temp,originalAuth,true);
            }
            ProjectFiles.DeleteOwnedFolder(privateRoot, home, token);
            gate.Dispose(); return ValueTask.CompletedTask;
        }
    }
}

// JSONL framing spans arbitrary pipe chunks; a malformed/truncated event fails the turn.
public sealed class CodexEvents(string? expectedThread, int maxCalls, long? tokenLimit,
    Action<string, string> observe, Action stop)
{
    private readonly StringBuilder pending = new();
    private readonly HashSet<string> calls = [];
    public string? Thread { get; private set; }
    public bool Completed { get; private set; }
    public string Message { get; private set; } = "";
    public string? Failure { get; private set; }
    public long? InputTokens { get; private set; }
    public long? CachedTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public int ToolCalls => calls.Count;
    public void Feed(string chunk)
    {
        foreach (char c in chunk)
        {
            if (c == '\n') { Parse(pending.ToString().TrimEnd('\r')); pending.Clear(); }
            else pending.Append(c);
            if (pending.Length > 8 * 1024 * 1024) { Fail("OversizedEvent"); pending.Clear(); return; }
        }
    }
    public void Finish() { if (pending.Length > 0) Parse(pending.ToString()); if (Thread is null || !Completed) Failure ??= "IncompleteTurn"; }
    private void Fail(string code) { Failure ??= code; stop(); }
    private void Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement; string type = root.GetProperty("type").GetString()!;
            observe(type, EventJournal.Redact(line));
            if (type == "thread.started")
            {
                string id = root.GetProperty("thread_id").GetString()!;
                if (!Guid.TryParse(id, out _) || expectedThread is not null && expectedThread != id || Thread is not null && Thread != id) Fail("ForeignThread");
                Thread = id;
            }
            else if (type == "turn.completed")
            {
                if (Completed) { Fail("DuplicateCompletion"); return; }
                Completed = true;
                if (root.TryGetProperty("usage", out var usage))
                {
                    long? Read(string key) => usage.TryGetProperty(key, out var value) && value.TryGetInt64(out long n) && n >= 0 ? n : null;
                    InputTokens = Read("input_tokens"); CachedTokens = Read("cached_input_tokens"); OutputTokens = Read("output_tokens");
                    if (tokenLimit is { } limit && InputTokens is { } input && OutputTokens is { } output && input + output > limit) Fail("ReportedTokenBudgetExceeded");
                }
            }
            else if (type is "turn.failed" or "error") Fail("ProviderError");
            else if (type.StartsWith("item.", StringComparison.Ordinal) && root.TryGetProperty("item", out var item))
            {
                string kind = item.GetProperty("type").GetString()!;
                if (kind is "command_execution" or "mcp_tool_call" or "web_search")
                {
                    calls.Add(item.GetProperty("id").GetString()!);
                    if (calls.Count > maxCalls) Fail("ToolCallBudgetExceeded");
                }
                if (type == "item.completed" && kind == "agent_message") Message = EventJournal.Redact(item.GetProperty("text").GetString()!);
            }
            // New informational item kinds are retained without inventing token usage or results.
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { Fail("MalformedEvent"); }
    }
}
