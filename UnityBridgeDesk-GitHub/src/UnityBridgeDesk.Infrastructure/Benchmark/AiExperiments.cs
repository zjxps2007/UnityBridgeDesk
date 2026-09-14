using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.Benchmark;

public static class AiTasks
{
    public static string[] Instructions(string id) => id switch
    {
        "A01" => ["Assets/DeskTask.unity 장면을 제작하고 저장하세요. y=0의 넓은 바닥 Floor, 위치 (0,3,0)·크기 (1,1,1)의 Cube 한 개와 카메라가 필요합니다. Cube는 동적 Rigidbody와 중력, Collider로 실제로 떨어져 바닥에 안정됩니다. Floor는 Trigger가 아닌 Collider입니다. Play 상태를 종료하고 저장하세요."],
        "A02" => ["Assets/DeskTask.unity에 하나의 큐브 프리팹으로 100개 인스턴스를 만들고 저장하세요. 이름은 Item_000부터 Item_099입니다. ID i의 위치는 ((i%10)*2,0,(i/10)*2), 색은 흰색, BoxCollider.size는 (1,1,1)입니다. 모두 같은 프리팹과 연결되어야 합니다.",
            "Item_000부터 Item_019만 색을 빨강으로, 높이를 y=1로 변경하고 저장하세요. 프리팹 연결과 나머지 격자는 유지하세요.",
            "기본 프리팹의 BoxCollider.size를 (2,1,1)로 변경해 모든 인스턴스에 전파하세요. 앞선 20개의 빨강·높이 예외와 프리팹 연결을 유지하고 저장하세요."],
        "A03" => ["Assets/DeskTask.unity에 물리 실험 장면과 uGUI 조작 화면을 제작하고 저장하세요. 넓은 Floor 바닥은 y=0, 큐브는 Cube_00부터 Cube_15입니다. i번째 큐브 초기 위치는 ((i%4)*2,6,(i/4)*2), 크기 1, Rigidbody와 Collider가 있습니다. 처음에는 움직이지 않습니다. 이름 Start인 Button으로 낙하를 시작하고 Reset인 Button으로 위치·선속도·각속도·집계·대기 상태를 복원합니다. Count라는 화면 텍스트에는 바닥에 닿은 큐브 수를 정수만 표시하세요. 각 큐브의 첫 바닥 접촉을 실행 주기마다 한 번만 세고, 지속·재접촉은 중복 집계하지 마세요. 한 주기가 끝나면 16입니다. 두 번 이상 다시 실행할 수 있어야 합니다. Play를 종료하고 저장하세요.",
            "기존 기능을 유지하면서 Pause·Resume이라는 uGUI Button을 추가하세요. 실행 중 Pause는 움직임과 집계를 멈추고 Resume은 이어서 실행합니다. Pause 상태에서도 Reset은 위치·속도·집계를 초기화하고 대기로 돌아가야 합니다. 대기 중 Pause/Resume은 아무 변화가 없어야 합니다. 저장 후 Play를 종료하세요."],
        _ => throw new ArgumentException("Unknown AI task")
    };
}
public sealed class AiExperiments(BridgeAdapter bridge, IAiRunner ai)
{
    public async Task<ExperimentMeasurement> RunAsync(BridgeTarget target, ExperimentCase experiment, BenchOptions options,
        AiOptions profile, string privateRoot, Action<string,string> observe, CancellationToken ct)
    {
        await using var session = await ai.CreateAsync(profile, target, privateRoot, ct);
        var all = Stopwatch.StartNew(); double aiMs = 0, validationMs = 0;
        long? input = 0, output = 0; int calls = 0, attempts = 0; bool firstPass = true;
        var instructions = AiTasks.Instructions(experiment.Id);
        string validator=Path.Combine(target.ProjectPath,"Assets","DeskValidation","Editor","DeskValidation.cs");
        string validatorHash=ProjectFiles.HashFile(validator);
        for (int stage = 1; stage <= instructions.Length; stage++)
        {
            string prompt = instructions[stage-1]; bool passed = false;
            for (int repair = 0; repair <= options.RepairAttempts; repair++)
            {
                attempts++;
                var work = Stopwatch.StartNew(); var result = await session.SendAsync(prompt, observe, ct); aiMs += work.Elapsed.TotalMilliseconds;
                input = input is { } i && result.InputTokens is { } ri ? i + ri : null;
                output = output is { } o && result.OutputTokens is { } ro ? o + ro : null;
                calls += result.ToolCalls;
                observe("ai.attempt",JsonSerializer.Serialize(new{stage,repair,attempts,result.Completed,result.InputTokens,result.OutputTokens,result.ToolCalls,aiMs}));
                if (!result.Completed) throw new IOException("AI 실행 실패: " + result.Failure);
                if(ProjectFiles.HashFile(validator)!=validatorHash)throw new InvalidDataException("독립 검증 파일이 변경되어 검사를 중지했습니다.");
                var verify = Stopwatch.StartNew();
                var errors = await ValidateAsync(target, experiment.Id, stage, options, observe, ct);
                validationMs += verify.Elapsed.TotalMilliseconds;
                if (errors.Length == 0) { passed = true; break; }
                firstPass = false;
                prompt = "같은 과제의 독립 검증이 실패했습니다. 다음 항목만 수정하고 저장한 뒤 Play를 종료하세요.\n" + string.Join("\n",errors);
            }
            if (!passed) throw new InvalidDataException("AI 결과가 독립 검증을 통과하지 못했습니다. validation 이벤트를 확인하세요.");
        }
        return new(0, aiMs, validationMs, [], firstPass, attempts, input, output, calls, all.Elapsed.TotalMilliseconds);
    }
    private async Task<string[]> ValidateAsync(BridgeTarget target, string id, int stage, BenchOptions options,
        Action<string,string> observe, CancellationToken ct)
    {
        var errors = new List<string>(); var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        async Task Check(string mode)
        {
            string nonce = Guid.NewGuid().ToString("N");
            var reply = await bridge.CallAsync(target, ["call", "desk_validate", "--params", JsonSerializer.Serialize(new { task = id, stage, mode, nonce })], timeout,
                frame => observe("validator." + frame.Kind, frame.Text), ct);
            var data = reply.Json.GetProperty("data");
            if (data.GetProperty("nonce").GetString() != nonce || data.GetProperty("pid").GetInt32() != reply.Before.Pid ||
                !InstanceDiscovery.SamePath(data.GetProperty("projectPath").GetString()!, target.ProjectPath)) throw new InvalidDataException("검증 응답 대상 불일치");
            observe("validation", data.ToString());
            if (!data.GetProperty("passed").GetBoolean()) errors.AddRange(data.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()!));
        }
        await bridge.CallAsync(target, ["editor", "stop", "--wait"], timeout, cancellationToken: ct, allowBusy: true);
        await Check("reopen");
        if (errors.Count == 0 && id is "A01" or "A03")
        {
            await bridge.CallAsync(target, ["editor", "play", "--wait"], timeout, cancellationToken: ct, allowBusy: true);
            await Check("exercise");
            await bridge.CallAsync(target, ["editor", "stop", "--wait"], timeout, cancellationToken: ct, allowBusy: true);
            await Check("reopen");
        }
        return [..errors];
    }
}
