namespace DigitalTwinSync.Experiment;

public sealed record ExperimentRequest
{
    public int RunsPerScenario { get; init; } = 10_000;
    public int Seed { get; init; } = 20_260_809;
    public int ProbeIntervalMs { get; init; } = 5_000;
    public int MinRoundTripMs { get; init; } = 20;
    public int MaxRoundTripMs { get; init; } = 100;
    public int RetryCount { get; init; } = 3;
}

public enum ProtectionMode
{
    Hmac,
    HmacAndSequence,
    ProposedMethod
}

public enum ScenarioKind
{
    PayloadModification,
    Replay,
    IntermediateOmission,
    TerminalOmission,
    LocalStateCorruption,
    CheckpointReplay,
    FullBlackout
}

public sealed record TrialOutcome(
    bool Detected,
    bool CommunicationLoss,
    bool RecoveryAttempted,
    bool Recovered);

public sealed record ModeMetrics(
    ProtectionMode Mode,
    int Trials,
    int Detected,
    double DetectionRatePercent,
    int CommunicationLoss,
    double CommunicationLossRatePercent,
    int RecoveryAttempts,
    int SuccessfulRecoveries,
    double RecoverySuccessRatePercent);

public sealed record ScenarioMetrics(
    ScenarioKind Scenario,
    string Name,
    string Purpose,
    IReadOnlyList<ModeMetrics> Modes);

public sealed record LatencyMetrics(
    int Samples,
    double MeanMs,
    double MedianMs,
    double Percentile95Ms,
    double MaximumMs);

public sealed record ExperimentReport(
    DateTimeOffset GeneratedAtUtc,
    ExperimentRequest Parameters,
    IReadOnlyList<ScenarioMetrics> Scenarios,
    LatencyMetrics TerminalOmissionDetectionLatency,
    IReadOnlyList<string> Notes);
