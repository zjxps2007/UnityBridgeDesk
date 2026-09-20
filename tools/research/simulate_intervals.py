"""Offline ADEMP sensitivity study, stdlib only. Never launched by the benchmark UI.

The implemented paired Fieller equation is checked against the independently inverted
interval supplied with Desk. This study does not certify Unity measurements.
"""
import argparse
import hashlib
import importlib.machinery
import importlib.util
import json
import math
import random
import statistics as stat
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERIFIER = ROOT / "src/UnityBridgeDesk.Infrastructure/Assets/ResearchVerifier.py.txt"
loader = importlib.machinery.SourceFileLoader("desk_independent_verifier", str(VERIFIER))
spec = importlib.util.spec_from_loader(loader.name, loader)
verifier = importlib.util.module_from_spec(spec)
loader.exec_module(verifier)


def fieller(a, b):
    n = len(a)
    if n < 5:
        return None
    ma, mb = stat.mean(a), stat.mean(b)
    va, vb = stat.variance(a), stat.variance(b)
    cov = sum((x-ma)*(y-mb) for x, y in zip(a, b))/(n-1)
    q = verifier.student95(n-1)**2/n
    aa, bb, cc = ma*ma-q*va, ma*mb-q*cov, mb*mb-q*vb
    d = bb*bb-aa*cc
    if aa <= 0 or d < 0:
        return None
    return ((bb-math.sqrt(d))/aa, (bb+math.sqrt(d))/aa)


def generate(rng, kind, n):
    a, b = [], []
    rho, sigma = .75, .4
    ar_a, ar_b = rng.gauss(0, 1), rng.gauss(0, 1)
    session_a, session_b = 1., 1.
    target = .9 if kind == "known_difference" else 1.
    lognormal = lambda sd: math.exp(rng.gauss(-sd*sd/2, sd))
    for i in range(n):
        if kind == "gamma":
            x, y = rng.gammavariate(4, 25), rng.gammavariate(4, 25)
        elif kind == "long_tail":
            x = 100*lognormal(.2)*(10 if rng.random() < .05 else 1)/1.45
            y = 100*lognormal(.2)*(10 if rng.random() < .05 else 1)/1.45
        elif kind == "ar1":
            ar_a = rho*ar_a + math.sqrt(1-rho*rho)*rng.gauss(0, 1)
            ar_b = rho*ar_b + math.sqrt(1-rho*rho)*rng.gauss(0, 1)
            x, y = (100*math.exp(sigma*z-sigma*sigma/2) for z in (ar_a, ar_b))
        elif kind == "session_clusters":
            # Five independent sessions, several dependent project trials in each.
            if i % (n//5) == 0:
                session_a, session_b = lognormal(.45), lognormal(.45)
            x, y = 100*session_a*lognormal(.1), 100*session_b*lognormal(.1)
        else:
            x = 100*lognormal(.2)
            y = 100*target*lognormal(.5 if kind in ("unequal_variance", "selective_timeout") else .2)
        if kind == "selective_timeout" and y > 130:
            continue  # paired-complete selection, NOT a completed-time observation at 130ms
        a.append(x)
        b.append(y)
    # B is independent of A here, so the paired survivor target is analytically known.
    if kind == "selective_timeout":
        phi = lambda z: (1+math.erf(z/math.sqrt(2)))/2
        mu, sd = math.log(100)-.5*.5/2, .5
        z = (math.log(130)-mu)/sd
        target = phi(z-sd)/phi(z)
    return a, b, target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    parser.add_argument("--replications", type=int, default=1000)
    parser.add_argument("--pairs", type=int, default=30)
    parser.add_argument("--seed", type=int, default=20260921)
    args = parser.parse_args()
    if not 100 <= args.replications <= 100000 or not 10 <= args.pairs <= 100 or args.pairs % 5:
        parser.error("replications: 100..100000; pairs: 10..100 and a multiple of 5")
    args.output.mkdir(parents=True, exist_ok=False)
    scenarios = ["lognormal", "known_difference", "gamma", "unequal_variance", "long_tail", "ar1", "session_clusters", "selective_timeout"]
    plan = dict(aim="paired ratio-of-means interval sensitivity; NOT Unity validation",
                estimand="population ratio of arithmetic means; selective_timeout reports paired-survivor and unconditional targets separately",
                method="paired t/Fieller equation; insufficient or unbounded sets withheld, not treated as finite CIs",
                n=args.pairs, replications=args.replications, seed=args.seed, scenarios=scenarios,
                implementation_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                verifier_sha256=hashlib.sha256(VERIFIER.read_bytes()).hexdigest(),
                metrics="finite interval and correct finite interval rates over ALL replicates; conditional finite coverage; width; bias; false-direction rate; Monte Carlo SE",
                exclusions="none; failed finite estimates reported, no parameter tuning from results")
    (args.output/"plan.json").write_text(json.dumps(plan, indent=2), encoding="utf-8")
    rows = []
    for index, kind in enumerate(scenarios):
        rng = random.Random(args.seed + index)
        finite = covered = false_direction = unconditional_covered = 0
        widths, ratios, valid_counts = [], [], []
        for repetition in range(args.replications):
            a, b, target = generate(rng, kind, args.pairs)
            valid_counts.append(len(a))
            interval = fieller(a, b)
            if a:
                ratios.append(stat.mean(b)/stat.mean(a))
            if repetition == 0:
                check = verifier.ratio_interval(a, b)
                assert (interval is None) == (check["Lower"] is None)
                if interval:
                    assert math.isclose(interval[0], check["Lower"], rel_tol=1e-7, abs_tol=1e-7)
                    assert math.isclose(interval[1], check["Upper"], rel_tol=1e-7, abs_tol=1e-7)
            if interval is None:
                continue
            finite += 1
            lo, hi = interval
            covered += lo <= target <= hi
            unconditional_covered += lo <= (0.9 if kind == "known_difference" else 1) <= hi
            widths.append(hi-lo)
            false_direction += (hi < 1 or lo > 1) if target == 1 else (lo > 1 if target < 1 else hi < 1)
        rate = covered/args.replications
        rows.append(dict(scenario=kind, target_ratio=target, replications=args.replications,
                         mean_valid_pairs=stat.mean(valid_counts), finite_count=finite,
                         unavailable_count=args.replications-finite,
                         correct_finite_rate=rate, monte_carlo_se=math.sqrt(rate*(1-rate)/args.replications),
                         coverage_given_finite=covered/finite if finite else None,
                         unconditional_target_correct_finite_rate=unconditional_covered/args.replications,
                         mean_ratio_bias=stat.mean(ratios)-target if ratios else None,
                         mean_finite_width=stat.mean(widths) if widths else None,
                         false_direction_rate=false_direction/args.replications))
    (args.output/"results.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    lines = ["Desk 통계 민감도 모의실험", f"시나리오당 {args.replications}회 · 계획 {args.pairs}쌍 · 시드 {args.seed}",
             "실제 Unity 자료가 아니다. 유한 구간 미제공도 전체 반복 분모에 남긴다.",
             "아래 비율은 전체 반복 중 참값을 담은 유한 구간의 비율이며, 미제공 구간을 포함률에서 몰래 제외하지 않는다.", ""]
    for row in rows:
        lines.append(f"{row['scenario']}: 올바른 유한 구간 {row['correct_finite_rate']:.1%} (Monte Carlo SE {row['monte_carlo_se']*100:.2f}%p), 미제공 {row['unavailable_count']}/{args.replications}, 평균 유효 {row['mean_valid_pairs']:.1f}쌍")
    lines.extend(["", "상관·세션 군집에서는 독립 시행 가정이 어긋난다. 반복 수만 늘리는 방식으로 해결되었다고 판단하지 않는다.",
                  "selective_timeout은 성공쌍 조건부 참값을 사용한다. 전체 요청의 참값에 대한 비율은 JSON의 unconditional_target_correct_finite_rate로 따로 기록한다.",
                  "계획·원시 요약은 plan.json, results.json에 보관한다. 실제 예비 분포 기반 설계·블록/세션 부트스트랩 비교는 후속 연구로 남는다."])
    (args.output/"00_모의실험요약.txt").write_text("\n".join(lines), encoding="utf-8-sig")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
