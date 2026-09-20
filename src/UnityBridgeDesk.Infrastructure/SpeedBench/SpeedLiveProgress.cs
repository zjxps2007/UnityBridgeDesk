namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public enum SpeedLiveStage { Preparation, Unity, Measurement, Cleanup, Finished }

/// <summary>Observed lifecycle state, separate from the measured command timings.</summary>
public sealed record SpeedLiveProgress(SpeedLiveStage Stage, int Completed, int Total, SpeedTrial? Trial,
    SpeedTrialResult? Result = null, SpeedTrial[]? Plan = null);
