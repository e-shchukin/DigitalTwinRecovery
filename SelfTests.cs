using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace DigitalTwinResearch;

public static class SelfTests
{
    public static void CrashWorker(string path, CommitPoint point)
    {
        var old = FileRecordStore.Load(path); var state = old.State with { Pressure = old.State.Pressure + 10 };
        var next = old.Version with { Sequence = old.Version.Sequence + 1 };
        var store = new FileRecordStore(path, old, reached => { if (reached == point) Environment.Exit(73); });
        store.Commit(new(state, next, Wire.Hash(state), next)); Environment.Exit(74);
    }
    public static void Run()
    {
        var state = TwinData.Create(42); var key = Wire.ResearchKey(42); var source = new TrustedSource("TWIN", state, key); var clock = new ManualClock();
        var store = new MemoryRecordStore(LocalRecord.Initial(state)); var agent = new RecoveryAgent("TWIN", store, key, RecoveryProtection.Full, clock, new ResearchNonce("tests"));
        var request = agent.Begin(); source.Update(new Delta(Pressure: state.Pressure + 1)); var checkpoint = source.Checkpoint(request);
        source.Update(new Delta(FlowRate: state.FlowRate + 1)); agent.ObserveKnownVersion(source.Read().Version);
        Check.That(agent.Receive(checkpoint) == "Rollback" && agent.Record.Version.Sequence == 0, "snapshot older than authenticated knowledge rejected");
        Check.That(agent.Record.MinimumVersion.Sequence == 2, "known floor persists while local state is older");
        Check.That(agent.Receive(source.Checkpoint(request)) == "Accepted", "current snapshot accepted after rejection");
        Check.That(agent.Receive(source.Checkpoint(request)) == "NoCurrentRequest", "closed nonce cannot install again");
        Check.That(agent.Record.IsConsistent && agent.Record.Version == source.Read().Version, "installed state and metadata form one record");
        request = agent.Begin(); Check.That(agent.Update(source.Update(new Delta(Temperature: state.Temperature + 1))) == "RecoveryInProgress", "updates fenced during recovery");
        clock.MoveTo(1000); Check.That(agent.Receive(source.Checkpoint(request)) == "Expired", "expired snapshot rejected");
        Check.That(agent.State == RecoveryState.CommunicationLoss, "timeout has explicit state");
        request = agent.Begin(); Check.That(agent.Receive(source.Checkpoint(request)) == "Accepted", "new recovery after timeout succeeds");
        var previousVersion = agent.Record.Version; var delta = source.Update(new Delta(FlowRate: state.FlowRate + 2));
        Check.That(agent.Update(delta) == "Accepted" && agent.Record.Version.Sequence == previousVersion.Sequence + 1, "deltas resume from installed base");
        agent.ObserveKnownVersion(new(2, 0));
        Check.That(agent.State == RecoveryState.NeedsRecovery && agent.Update(source.Update(new Delta(Pressure: 123))) == "RecoveryInProgress", "new authenticated knowledge fences an older local base");
        source.NewEpoch(state); request = agent.Begin(); Check.That(agent.Receive(source.Checkpoint(request)) == "Accepted" && agent.Record.Version == new VersionStamp(2, 0), "new epoch full snapshot accepted despite smaller Seq");

        var root = Path.Combine(Path.GetTempPath(), "digital-twin-crash-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var point in Enum.GetValues<CommitPoint>())
            {
                var path = Path.Combine(root, point + ".json"); var initial = LocalRecord.Initial(state); new FileRecordStore(path, initial);
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(executable) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--crash-worker"); start.ArgumentList.Add(path); start.ArgumentList.Add(point.ToString());
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start worker.");
                if (!child.WaitForExit(15000)) { child.Kill(entireProcessTree: true); throw new InvalidOperationException("Crash worker timed out."); }
                Check.That(child.ExitCode == 73, "worker really terminated at " + point + "; " + child.StandardError.ReadToEnd());
                var loaded = FileRecordStore.Load(path); var shouldBeNew = point == CommitPoint.AfterRenameBeforePublish;
                Check.That(loaded.IsConsistent && loaded.Version.Sequence == (shouldBeNew ? 1 : 0), "restart sees complete old or new record at " + point);
                Check.That(loaded.State.Pressure == state.Pressure + (shouldBeNew ? 10 : 0), "state matches version after " + point);
            }
            var floorPath = Path.Combine(root, "floor.json"); var file = new FileRecordStore(floorPath, LocalRecord.Initial(state));
            agent = new("TWIN", file, key, RecoveryProtection.Full, new ManualClock()); agent.Begin(new(2, 0));
            Check.That(FileRecordStore.Load(floorPath).MinimumVersion == new VersionStamp(2, 0), "known floor survives reopening");
            foreach (var failAt in new[] { CommitPoint.AfterFlushBeforeRename, CommitPoint.AfterRenameBeforePublish })
            {
                var failOnce = true; var failurePath = Path.Combine(root, "io-" + failAt + ".json");
                var failureStore = new FileRecordStore(failurePath, LocalRecord.Initial(state), point =>
                { if (point == failAt && failOnce) { failOnce = false; throw new IOException("Injected I/O failure"); } });
                var failureSource = new TrustedSource("TWIN", state, key); failureSource.Update(new Delta(Pressure: state.Pressure + 10));
                var failureAgent = new RecoveryAgent("TWIN", failureStore, key, RecoveryProtection.Full, new ManualClock());
                var failureRequest = failureAgent.Begin(); var failureSnapshot = failureSource.Checkpoint(failureRequest);
                Check.That(failureAgent.Receive(failureSnapshot) == "InstallationFailed", "I/O failure has an explicit result at " + failAt);
                Check.That(failureAgent.Record.IsConsistent && failureAgent.Record.Version.Sequence == (failAt == CommitPoint.AfterRenameBeforePublish ? 1 : 0), "I/O exception reloads the complete durable record");
                Check.That(failureAgent.Receive(failureSnapshot) == "NoCurrentRequest", "uncertain install consumes request");
                Check.That(failureAgent.Update(failureSource.Update(new Delta(FlowRate: 777))) == "RecoveryInProgress", "uncertain install fences deltas");
                var retry = failureAgent.Begin(); Check.That(failureAgent.Receive(failureSource.Checkpoint(retry)) == "Accepted", "new request recovers after I/O failure");
            }
            // A deliberate non-atomic counterexample: new version with old state.
            var unsafeStatePath = Path.Combine(root, "unsafe-state.json"); var unsafeVersionPath = Path.Combine(root, "unsafe-version.json");
            File.WriteAllText(unsafeStatePath, JsonSerializer.Serialize(state)); File.WriteAllText(unsafeVersionPath, "1");
            var unsafeState = JsonSerializer.Deserialize<TwinData>(File.ReadAllText(unsafeStatePath))!;
            Check.That(!Check.SameState(unsafeState, state with { Pressure = state.Pressure + 10 }) && File.ReadAllText(unsafeVersionPath) == "1", "split persistence has a mixed-state counterexample for the planned version-1 state");

            var memory = new MemoryRecordStore(LocalRecord.Initial(state)); var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            { for (var i = 0; i < 2000; i++) Check.That(memory.Read().IsConsistent, "concurrent reader sees consistent tuple"); })).ToArray();
            for (var i = 1; i <= 2000; i++) { var data = state with { Pressure = i }; var v = new VersionStamp(1, i); memory.Commit(new(data, v, Wire.Hash(data), v)); }
            Task.WaitAll(readers);
        }
        finally { Directory.Delete(root, true); }
        var report = RecoveryExperiment.Run(new(Runs: 3, BenchmarkRepeats: 1));
        Check.That(report.Functional.Count == 36, "all recovery scenarios and three ablations executed");
        foreach (var row in report.Functional.Where(x => x.Mode == RecoveryProtection.Full))
        {
            var shouldAccept = row.Scenario is "ValidSnapshot" or "SameVersionRepair" or "NewEpoch" or "UpdateDuringRecovery";
            Check.That(row.Accepted == (shouldAccept ? 3 : 0), row.Scenario + " expected admission");
            Check.That(row.StatePreservedOnRejection == row.Rejected, row.Scenario + " rejection does not replace state");
            Check.That(row.CorrectAfterAccepted == row.Accepted, row.Scenario + " accepted snapshot installs its exact version");
            Check.That(row.BelowKnownFloor == 0 && row.WrongSessionAccepted == 0, row.Scenario + " full method respects floor and session");
        }
        Check.That(report.Functional.Single(x => x.Scenario == "OlderThanKnownVersion" && x.Mode == RecoveryProtection.HmacVersion).Accepted == 3, "local-version-only ablation exposes known-floor gap");
        Check.That(report.Functional.Single(x => x.Scenario == "WrongSession" && x.Mode == RecoveryProtection.HmacVersion).Accepted == 3, "version-only ablation admits old session");
        Console.WriteLine($"PASS: {Check.Count} checks; includes four actual process terminations and concurrent tuple readers.");
    }
}
