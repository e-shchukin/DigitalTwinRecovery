using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DigitalTwinSync.Experiment;

public sealed class ExperimentRunner
{
    private static readonly ProtectionMode[] Modes =
    {
        ProtectionMode.Hmac,
        ProtectionMode.HmacAndSequence,
        ProtectionMode.ProposedMethod
    };

    private static readonly ScenarioKind[] Scenarios =
    {
        ScenarioKind.PayloadModification,
        ScenarioKind.Replay,
        ScenarioKind.IntermediateOmission,
        ScenarioKind.TerminalOmission,
        ScenarioKind.LocalStateCorruption,
        ScenarioKind.CheckpointReplay,
        ScenarioKind.FullBlackout
    };

    public ExperimentReport Run(ExperimentRequest request)
    {
        Validate(request);

        var scenarioReports = new List<ScenarioMetrics>();

        foreach (var scenario in Scenarios)
        {
            var modeReports = new List<ModeMetrics>();

            foreach (var mode in Modes)
            {
                var detected = 0;
                var communicationLoss = 0;
                var recoveryAttempts = 0;
                var successfulRecoveries = 0;

                for (var trial = 0; trial < request.RunsPerScenario; trial++)
                {
                    var random = CreateRandom(request.Seed, scenario, mode, trial);
                    var outcome = RunTrial(scenario, mode, random, request);

                    detected += outcome.Detected ? 1 : 0;
                    communicationLoss += outcome.CommunicationLoss ? 1 : 0;
                    recoveryAttempts += outcome.RecoveryAttempted ? 1 : 0;
                    successfulRecoveries += outcome.Recovered ? 1 : 0;
                }

                modeReports.Add(new ModeMetrics(
                    mode,
                    request.RunsPerScenario,
                    detected,
                    Percent(detected, request.RunsPerScenario),
                    communicationLoss,
                    Percent(communicationLoss, request.RunsPerScenario),
                    recoveryAttempts,
                    successfulRecoveries,
                    Percent(successfulRecoveries, recoveryAttempts)));
            }

            scenarioReports.Add(new ScenarioMetrics(
                scenario,
                ScenarioName(scenario),
                ScenarioPurpose(scenario),
                modeReports));
        }

        return new ExperimentReport(
            DateTimeOffset.UtcNow,
            request,
            scenarioReports,
            CalculateTerminalOmissionLatency(request),
            new[]
            {
                "Сценарий повреждения локального состояния выходит за основную модель недоверенного узла и проверяет назначение ResultHash.",
                "В сценарии полного подавления канала разработанный метод фиксирует COMM_LOSS, но не объявляет доказанную рассинхронизацию или DoS.",
                "Модель не использует реальные задержки Thread.Sleep: время выявления рассчитывается дискретно, поэтому число прогонов не увеличивает продолжительность эксперимента линейно с T_probe.",
                "Полученные результаты относятся к программной модели протокола; сетевые, процессорные и дисковые накладные расходы требуют отдельного стендового измерения."
            });
    }

    private static TrialOutcome RunTrial(
        ScenarioKind scenario,
        ProtectionMode mode,
        Random random,
        ExperimentRequest request)
    {
        var twinId = "KII-TWIN-01";
        var initialState = new TwinState(
            Pressure: random.Next(90, 111),
            Temperature: random.Next(60, 91),
            ValveOpen: random.Next(0, 2) == 1,
            FlowRate: random.Next(30, 71));
        var key = DeriveKey(request.Seed, scenario, mode, random.Next());
        var source = new SourceSimulator(twinId, initialState, key);
        var twin = new TwinSimulator(twinId, initialState, key);

        return scenario switch
        {
            ScenarioKind.PayloadModification => PayloadModification(source, twin, mode),
            ScenarioKind.Replay => Replay(source, twin, mode),
            ScenarioKind.IntermediateOmission => IntermediateOmission(source, twin, mode),
            ScenarioKind.TerminalOmission => TerminalOmission(source, twin, mode, random),
            ScenarioKind.LocalStateCorruption => LocalStateCorruption(source, twin, mode),
            ScenarioKind.CheckpointReplay => CheckpointReplay(source, twin, mode, random),
            ScenarioKind.FullBlackout => FullBlackout(source, twin, mode, request.RetryCount),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static TrialOutcome PayloadModification(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode)
    {
        var update = source.CreateUpdate(new StateDelta(Pressure: source.State.Pressure + 1));
        var tampered = update with
        {
            Delta = new StateDelta(Pressure: source.State.Pressure + 500)
        };

        var result = twin.ReceiveUpdate(tampered, mode);
        return new TrialOutcome(
            Detected: result == UpdateAcceptance.AuthenticationFailed,
            CommunicationLoss: false,
            RecoveryAttempted: false,
            Recovered: false);
    }

    private static TrialOutcome Replay(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode)
    {
        var update = source.CreateUpdate(new StateDelta(ValveOpen: !source.State.ValveOpen));
        twin.ReceiveUpdate(update, mode);
        var replayResult = twin.ReceiveUpdate(update, mode);

        return new TrialOutcome(
            Detected: replayResult != UpdateAcceptance.Accepted,
            CommunicationLoss: false,
            RecoveryAttempted: false,
            Recovered: false);
    }

    private static TrialOutcome IntermediateOmission(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode)
    {
        source.CreateUpdate(new StateDelta(Pressure: source.State.Pressure + 1));
        var second = source.CreateUpdate(new StateDelta(FlowRate: source.State.FlowRate + 2));
        var result = twin.ReceiveUpdate(second, mode);
        var detected = result != UpdateAcceptance.Accepted;

        return mode == ProtectionMode.ProposedMethod && detected
            ? Recover(source, twin, detected)
            : new TrialOutcome(detected, false, false, false);
    }

    private static TrialOutcome TerminalOmission(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode,
        Random random)
    {
        source.CreateUpdate(new StateDelta(Temperature: source.State.Temperature + 1));

        if (mode != ProtectionMode.ProposedMethod)
        {
            return new TrialOutcome(false, false, false, false);
        }

        var nonce = Nonce(random);
        var probe = twin.CreateProbe(nonce);
        var status = source.CreateStatus(probe);
        var detected = twin.EvaluateStatus(status, nonce) == StatusEvaluation.Desynchronized;

        return detected
            ? Recover(source, twin, detected)
            : new TrialOutcome(false, false, false, false);
    }

    private static TrialOutcome LocalStateCorruption(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode)
    {
        var first = source.CreateUpdate(new StateDelta(Pressure: source.State.Pressure + 1));
        twin.ReceiveUpdate(first, mode);
        twin.CorruptState(twin.State with { Temperature = twin.State.Temperature + 777 });
        var second = source.CreateUpdate(new StateDelta(FlowRate: source.State.FlowRate + 1));
        var result = twin.ReceiveUpdate(second, mode);
        var detected = result == UpdateAcceptance.ResultHashMismatch;

        return mode == ProtectionMode.ProposedMethod && detected
            ? Recover(source, twin, detected)
            : new TrialOutcome(detected, false, false, false);
    }

    private static TrialOutcome CheckpointReplay(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode,
        Random random)
    {
        if (mode != ProtectionMode.ProposedMethod)
        {
            return new TrialOutcome(false, false, false, false);
        }

        source.CreateUpdate(new StateDelta(ValveOpen: !source.State.ValveOpen));
        var oldNonce = Nonce(random);
        var oldRequest = twin.CreateResyncRequest(oldNonce);
        var oldCheckpoint = source.CreateCheckpoint(oldRequest);

        var currentNonce = Nonce(random);
        var currentRequest = twin.CreateResyncRequest(currentNonce);
        var replayResult = twin.ReceiveCheckpoint(oldCheckpoint, currentNonce);
        var detected = replayResult == CheckpointAcceptance.NonceMismatch;

        var currentCheckpoint = source.CreateCheckpoint(currentRequest);
        var recovered = twin.ReceiveCheckpoint(currentCheckpoint, currentNonce) == CheckpointAcceptance.Accepted &&
                        twin.State == source.State;

        return new TrialOutcome(detected, false, true, recovered);
    }

    private static TrialOutcome FullBlackout(
        SourceSimulator source,
        TwinSimulator twin,
        ProtectionMode mode,
        int retryCount)
    {
        source.CreateUpdate(new StateDelta(Pressure: source.State.Pressure + 1));

        if (mode != ProtectionMode.ProposedMethod)
        {
            return new TrialOutcome(false, false, false, false);
        }

        var attempts = 0;
        while (attempts < retryCount)
        {
            attempts++;
            // Шлюз подавляет как UPDATE, так и каждый SYNC_PROBE/SYNC_STATUS.
        }

        return new TrialOutcome(
            Detected: false,
            CommunicationLoss: attempts == retryCount,
            RecoveryAttempted: false,
            Recovered: false);
    }

    private static TrialOutcome Recover(
        SourceSimulator source,
        TwinSimulator twin,
        bool detected)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var request = twin.CreateResyncRequest(nonce);
        var checkpoint = source.CreateCheckpoint(request);
        var accepted = twin.ReceiveCheckpoint(checkpoint, nonce) == CheckpointAcceptance.Accepted;
        var recovered = accepted && twin.State == source.State && twin.Sequence == source.Sequence;
        return new TrialOutcome(detected, false, true, recovered);
    }

    private static LatencyMetrics CalculateTerminalOmissionLatency(ExperimentRequest request)
    {
        var random = new Random(unchecked(request.Seed * 397) ^ 0x5EED_482);
        var values = new double[request.RunsPerScenario];

        for (var index = 0; index < values.Length; index++)
        {
            var elapsedSincePreviousProbe = random.NextDouble() * request.ProbeIntervalMs;
            var untilNextProbe = request.ProbeIntervalMs - elapsedSincePreviousProbe;
            var roundTrip = request.MinRoundTripMs +
                            random.NextDouble() * (request.MaxRoundTripMs - request.MinRoundTripMs);
            values[index] = untilNextProbe + roundTrip;
        }

        Array.Sort(values);
        var mean = values.Average();
        var median = values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
        var percentile95Index = Math.Clamp(
            (int)Math.Ceiling(values.Length * 0.95) - 1,
            0,
            values.Length - 1);

        return new LatencyMetrics(
            values.Length,
            Math.Round(mean, 4),
            Math.Round(median, 4),
            Math.Round(values[percentile95Index], 4),
            Math.Round(values[^1], 4));
    }

    private static Random CreateRandom(
        int seed,
        ScenarioKind scenario,
        ProtectionMode mode,
        int trial)
    {
        var derived = unchecked(
            seed * 486_187_739 +
            (int)scenario * 16_777_619 +
            (int)mode * 65_537 +
            trial);
        return new Random(derived);
    }

    private static byte[] DeriveKey(
        int seed,
        ScenarioKind scenario,
        ProtectionMode mode,
        int salt)
    {
        var material = $"{seed}|{scenario}|{mode}|{salt}|research-only";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    private static string Nonce(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private static double Percent(int count, int total)
    {
        return total == 0
            ? 0
            : Math.Round(100.0 * count / total, 4);
    }

    private static string ScenarioName(ScenarioKind scenario)
    {
        return scenario switch
        {
            ScenarioKind.PayloadModification => "Модификация UPDATE",
            ScenarioKind.Replay => "Повтор ранее принятого UPDATE",
            ScenarioKind.IntermediateOmission => "Удаление промежуточного UPDATE",
            ScenarioKind.TerminalOmission => "Удаление последнего UPDATE",
            ScenarioKind.LocalStateCorruption => "Повреждение локального состояния ЦД",
            ScenarioKind.CheckpointReplay => "Повтор старого CHECKPOINT",
            ScenarioKind.FullBlackout => "Полное подавление канала",
            _ => scenario.ToString()
        };
    }

    private static string ScenarioPurpose(ScenarioKind scenario)
    {
        return scenario switch
        {
            ScenarioKind.PayloadModification => "Проверка HMAC-аутентификации полезной нагрузки.",
            ScenarioKind.Replay => "Проверка защиты Seq от повторной передачи.",
            ScenarioKind.IntermediateOmission => "Проверка обнаружения разрыва Seq.",
            ScenarioKind.TerminalOmission => "Проверка SYNC_PROBE при отсутствии следующего UPDATE.",
            ScenarioKind.LocalStateCorruption => "Проверка постусловия перехода по ResultHash.",
            ScenarioKind.CheckpointReplay => "Проверка связи CHECKPOINT с текущим nonce.",
            ScenarioKind.FullBlackout => "Проверка перехода в COMM_LOSS без ложной атрибуции DoS.",
            _ => string.Empty
        };
    }

    private static void Validate(ExperimentRequest request)
    {
        if (request.RunsPerScenario is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.RunsPerScenario),
                "Число прогонов должно находиться в диапазоне от 1 до 100000.");
        }

        if (request.ProbeIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ProbeIntervalMs),
                "Период SYNC_PROBE должен быть положительным.");
        }

        if (request.MinRoundTripMs < 0 || request.MaxRoundTripMs < request.MinRoundTripMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MinRoundTripMs),
                "Диапазон времени двустороннего обмена задан некорректно.");
        }

        if (request.RetryCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.RetryCount),
                "Число повторных попыток должно находиться в диапазоне от 1 до 100.");
        }
    }
}

public static class ExperimentCsv
{
    public static string Build(ExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Сценарий;Вариант защиты;Прогонов;Обнаружено;Доля обнаружения, %;COMM_LOSS;Доля COMM_LOSS, %;Попыток восстановления;Успешных восстановлений;Успех восстановления, %");

        foreach (var scenario in report.Scenarios)
        {
            foreach (var mode in scenario.Modes)
            {
                builder.Append(Escape(scenario.Name)).Append(';')
                    .Append(mode.Mode).Append(';')
                    .Append(mode.Trials).Append(';')
                    .Append(mode.Detected).Append(';')
                    .Append(Number(mode.DetectionRatePercent)).Append(';')
                    .Append(mode.CommunicationLoss).Append(';')
                    .Append(Number(mode.CommunicationLossRatePercent)).Append(';')
                    .Append(mode.RecoveryAttempts).Append(';')
                    .Append(mode.SuccessfulRecoveries).Append(';')
                    .Append(Number(mode.RecoverySuccessRatePercent))
                    .AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("Метрика задержки обнаружения удаления последнего UPDATE;Значение, мс");
        builder.AppendLine($"Количество прогонов;{report.TerminalOmissionDetectionLatency.Samples}");
        builder.AppendLine($"Среднее;{Number(report.TerminalOmissionDetectionLatency.MeanMs)}");
        builder.AppendLine($"Медиана;{Number(report.TerminalOmissionDetectionLatency.MedianMs)}");
        builder.AppendLine($"95-й процентиль;{Number(report.TerminalOmissionDetectionLatency.Percentile95Ms)}");
        builder.AppendLine($"Максимум;{Number(report.TerminalOmissionDetectionLatency.MaximumMs)}");

        return builder.ToString();
    }

    private static string Number(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return value.Contains(';') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
