using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalTwinResearch;

public interface IRecordStore { LocalRecord Read(); void Commit(LocalRecord record); }
public sealed class MemoryRecordStore(LocalRecord initial) : IRecordStore
{
    private LocalRecord _record = initial;
    public LocalRecord Read() => Volatile.Read(ref _record);
    public void Commit(LocalRecord record) { if (!record.IsConsistent) throw new InvalidDataException("Invalid record."); Interlocked.Exchange(ref _record, record); }
}

public enum CommitPoint { BeforeWrite, DuringWrite, AfterFlushBeforeRename, AfterRenameBeforePublish }
public sealed record DiskEnvelope(string Body, string Sha256);

// Single writer. Temporary file and destination are in the same directory.
// Tested contract: process crash on a local Linux filesystem, not power loss.
public sealed class FileRecordStore : IRecordStore
{
    private readonly object _gate = new(); private readonly string _path; private LocalRecord _record;
    private readonly Action<CommitPoint>? _fault;
    public FileRecordStore(string path, LocalRecord initial, Action<CommitPoint>? fault = null)
    {
        _path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(_path)!); _record = initial;
        if (File.Exists(_path)) _record = Load(_path); else Commit(initial);
        _fault = fault;
    }
    public LocalRecord Read() => Volatile.Read(ref _record);
    public static LocalRecord Load(string path)
    {
        var size = new FileInfo(path).Length; if (size is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("Invalid stored size.");
        var envelope = JsonSerializer.Deserialize<DiskEnvelope>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty store.");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Body)));
        if (digest != envelope.Sha256) throw new InvalidDataException("Corrupted store envelope.");
        var record = JsonSerializer.Deserialize<LocalRecord>(envelope.Body) ?? throw new InvalidDataException("Missing record.");
        if (!record.IsConsistent) throw new InvalidDataException("Inconsistent stored record."); return record;
    }
    public void Commit(LocalRecord record)
    {
        if (!record.IsConsistent) throw new InvalidDataException("Invalid record.");
        lock (_gate)
        {
            var body = JsonSerializer.Serialize(record);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new DiskEnvelope(body, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))));
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                _fault?.Invoke(CommitPoint.BeforeWrite);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes.AsSpan(0, bytes.Length / 2)); _fault?.Invoke(CommitPoint.DuringWrite);
                    stream.Write(bytes.AsSpan(bytes.Length / 2)); stream.Flush(flushToDisk: true);
                }
                _fault?.Invoke(CommitPoint.AfterFlushBeforeRename);
                File.Move(temporary, _path, overwrite: true);
                _fault?.Invoke(CommitPoint.AfterRenameBeforePublish);
                Interlocked.Exchange(ref _record, record);
            }
            catch
            {
                // An I/O exception after rename may mean the durable value changed.
                // Reload the complete record before exposing further operations.
                if (File.Exists(_path)) Interlocked.Exchange(ref _record, Load(_path)); throw;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

public enum RecoveryProtection { HmacOnly, HmacVersion, Full }
public enum RecoveryState { Ready, AwaitingSnapshot, NeedsRecovery, CommunicationLoss, InstallationFailed }
public sealed record RecoveryContext(string Nonce, double Deadline);

public sealed class RecoveryAgent
{
    private readonly object _gate = new(); private readonly string _id; private readonly byte[] _key; private readonly IClock _clock;
    private readonly INonceSource _nonces; private readonly IRecordStore _store; private readonly double _timeout;
    private RecoveryContext? _pending;
    public RecoveryProtection Mode { get; }
    public RecoveryState State { get; private set; } = RecoveryState.Ready;
    public LocalRecord Record => _store.Read();
    public RecoveryAgent(string id, IRecordStore store, byte[] key, RecoveryProtection mode, IClock clock, INonceSource? nonces = null, double timeout = 1000)
    {
        if (!double.IsFinite(timeout) || timeout <= 0) throw new ArgumentOutOfRangeException(nameof(timeout));
        _id = id; _key = key.ToArray(); _store = store; Mode = mode; _clock = clock; _nonces = nonces ?? new CryptoNonce(); _timeout = timeout;
        if (Record.Version.CompareTo(Record.MinimumVersion) < 0) State = RecoveryState.NeedsRecovery;
    }
    // knownVersion is trusted input from an already authenticated freshness check.
    // The network peer cannot set this floor through an unauthenticated message.
    public void ObserveKnownVersion(VersionStamp version)
    {
        lock (_gate)
        {
            if (!version.IsValid) throw new ArgumentException("Invalid known version.");
            var record = _store.Read(); var floor = VersionStamp.Max(record.MinimumVersion, version);
            if (floor != record.MinimumVersion) _store.Commit(record with { MinimumVersion = floor });
            if (floor.CompareTo(record.Version) > 0 && State == RecoveryState.Ready) State = RecoveryState.NeedsRecovery;
        }
    }
    public Packet Begin(VersionStamp? knownVersion = null)
    {
        lock (_gate)
        {
            var record = _store.Read();
            if (knownVersion.HasValue)
            {
                if (!knownVersion.Value.IsValid) throw new ArgumentException("Invalid known version.");
                record = record with { MinimumVersion = VersionStamp.Max(record.MinimumVersion, knownVersion.Value) }; _store.Commit(record);
            }
            var nonce = _nonces.Next(); _pending = new(nonce, _clock.NowMs + _timeout); State = RecoveryState.AwaitingSnapshot;
            return Wire.Control("RESYNC_REQUEST", _id, record.Version, nonce, Wire.Hash(record.State), _key);
        }
    }
    public void Tick()
    {
        lock (_gate) if (_pending is not null && _clock.NowMs >= _pending.Deadline) { _pending = null; State = RecoveryState.CommunicationLoss; }
    }
    public string Receive(Packet snapshot)
    {
        lock (_gate)
        {
            if (!Wire.Verify(snapshot, "CHECKPOINT", _id, _key)) return "AuthenticationOrType";
            if (_pending is null) return "NoCurrentRequest";
            if (_clock.NowMs >= _pending.Deadline) { _pending = null; State = RecoveryState.CommunicationLoss; return "Expired"; }
            if (Mode == RecoveryProtection.Full && snapshot.Nonce != _pending.Nonce) return "WrongSession";
            var old = _store.Read(); var floor = Mode == RecoveryProtection.Full ? old.MinimumVersion : old.Version;
            if (Mode != RecoveryProtection.HmacOnly && snapshot.Version.CompareTo(floor) < 0) return "Rollback";
            TwinData state; try { state = Wire.ReadState(snapshot.Payload); } catch (IOException) { return "MalformedState"; }
            var hash = Wire.Hash(state); if (Mode == RecoveryProtection.Full && hash != snapshot.Hash) return "ResultHash";
            var next = new LocalRecord(state, snapshot.Version, hash, VersionStamp.Max(old.MinimumVersion, snapshot.Version));
            // Consume the context before committing. An uncertain failed install requires a new request.
            _pending = null;
            try { _store.Commit(next); State = RecoveryState.Ready; return "Accepted"; }
            catch (IOException) { State = RecoveryState.InstallationFailed; return "InstallationFailed"; }
        }
    }
    public string Update(Packet update)
    {
        lock (_gate)
        {
            if (State != RecoveryState.Ready) return "RecoveryInProgress";
            if (!Wire.Verify(update, "UPDATE", _id, _key) || update.Nonce.Length != 0) return "AuthenticationOrType";
            var old = _store.Read();
            if (update.Version.Epoch != old.Version.Epoch || update.Version.Sequence != old.Version.Sequence + 1) return "SequenceOrEpoch";
            TwinData state; try { state = Wire.ReadDelta(update.Payload).Apply(old.State); } catch (IOException) { return "MalformedDelta"; }
            var hash = Wire.Hash(state); if (hash != update.Hash) return "ResultHash";
            var next = new LocalRecord(state, update.Version, hash, VersionStamp.Max(old.MinimumVersion, update.Version));
            try { _store.Commit(next); return "Accepted"; } catch (IOException) { State = RecoveryState.InstallationFailed; return "InstallationFailed"; }
        }
    }
}

public sealed record RecoveryParameters(int Runs = 1000, int Seed = 20260809, int BenchmarkRepeats = 30)
{
    public void Validate() { if (Runs is < 1 or > 10000 || BenchmarkRepeats is < 1 or > 200) throw new ArgumentException("Runs must be 1..10000, benchmark repeats 1..200."); }
}
public sealed record RecoveryRow(string Scenario, RecoveryProtection Mode, int Runs, int Accepted, int Rejected, int StatePreservedOnRejection, int CorrectAfterAccepted, int BelowKnownFloor, int WrongSessionAccepted);
public sealed record BenchmarkSample(string Store, int StateBytes, int Trial, double SnapshotCreationMs, double VerificationAndCommitMs);
public sealed record BenchmarkRow(string Store, int StateBytes, Summary SnapshotCreation, Summary VerificationAndCommit);
public sealed record RecoveryReport(string Project, string UpstreamCommit, RecoveryParameters Parameters, IReadOnlyList<RecoveryRow> Functional,
    IReadOnlyList<BenchmarkRow> Benchmark, IReadOnlyList<BenchmarkSample> Samples, string[] Limitations);

public static class RecoveryExperiment
{
    public static readonly string[] Scenarios = ["ValidSnapshot", "SameVersionRepair", "SnapshotTampering", "WrongSession", "Rollback",
        "OlderThanKnownVersion", "NewEpoch", "OldEpoch", "ClosedRequest", "ExpiredRequest", "IncorrectSourceHash", "UpdateDuringRecovery"];
    public static RecoveryReport Run(RecoveryParameters p, CancellationToken ct = default)
    {
        p.Validate(); var rows = new List<RecoveryRow>();
        foreach (var scenario in Scenarios) foreach (var mode in Enum.GetValues<RecoveryProtection>())
        {
            var accepted = 0; var rejected = 0; var preserved = 0; var correct = 0; var belowFloor = 0; var wrongSession = 0;
            for (var trial = 0; trial < p.Runs; trial++)
            {
                ct.ThrowIfCancellationRequested(); var state = TwinData.Create(unchecked(p.Seed + trial)); var key = Wire.ResearchKey(p.Seed);
                var source = new TrustedSource("TWIN", state, key); var clock = new ManualClock(); LocalRecord initial = LocalRecord.Initial(state);
                if (scenario == "SameVersionRepair") { var bad = state with { Pressure = state.Pressure + 777 }; initial = initial with { State = bad, Hash = Wire.Hash(bad) }; }
                var store = new MemoryRecordStore(initial); var agent = new RecoveryAgent("TWIN", store, key, mode, clock, new ResearchNonce($"{p.Seed}/{scenario}/{trial}"));
                Packet request, snapshot;
                if (scenario == "Rollback")
                { var oldRequest = agent.Begin(); snapshot = source.Checkpoint(oldRequest); agent.Receive(snapshot); agent.Update(source.Update(new Delta(FlowRate: state.FlowRate + 1))); request = agent.Begin(); }
                else if (scenario == "OlderThanKnownVersion")
                { request = agent.Begin(); source.Update(new Delta(Pressure: state.Pressure + 1)); snapshot = source.Checkpoint(request); source.Update(new Delta(FlowRate: state.FlowRate + 1)); agent.ObserveKnownVersion(source.Read().Version); }
                else if (scenario == "OldEpoch")
                { var early = agent.Begin(); snapshot = source.Checkpoint(early); source.NewEpoch(state with { Temperature = state.Temperature + 1 }); agent.Receive(source.Checkpoint(early)); request = agent.Begin(); }
                else
                {
                    if (scenario != "SameVersionRepair") source.Update(new Delta(Pressure: state.Pressure + 1));
                    if (scenario == "NewEpoch") source.NewEpoch(state with { FlowRate = state.FlowRate + 2 });
                    request = agent.Begin(); snapshot = source.Checkpoint(request);
                    if (scenario == "SnapshotTampering") { var bytes = snapshot.Payload.ToArray(); bytes[0] ^= 1; snapshot = snapshot with { Payload = bytes }; }
                    if (scenario == "WrongSession") { var previous = snapshot; request = agent.Begin(); snapshot = previous; }
                    if (scenario == "ClosedRequest") agent.Receive(snapshot);
                    if (scenario == "ExpiredRequest") clock.MoveTo(1000);
                    if (scenario == "IncorrectSourceHash") snapshot = Wire.Sign(snapshot with { Hash = new string('0', 64) }, key); // diagnostic: faulty trusted producer, not a forged MAC
                    if (scenario == "UpdateDuringRecovery")
                    { var update = source.Update(new Delta(Temperature: state.Temperature + 1)); Check.That(agent.Update(update) == "RecoveryInProgress", "deltas fenced while awaiting snapshot"); }
                }
                var before = agent.Record; var result = agent.Receive(snapshot); var after = agent.Record;
                if (result == "Accepted")
                {
                    accepted++; if (Check.SameState(after.State, Wire.ReadState(snapshot.Payload)) && after.Version == snapshot.Version && after.IsConsistent) correct++;
                    if (after.Version.CompareTo(before.MinimumVersion) < 0) belowFloor++;
                    if (snapshot.Nonce != request.Nonce) wrongSession++;
                }
                else { rejected++; if (ReferenceEquals(before, after)) preserved++; }
            }
            rows.Add(new(scenario, mode, p.Runs, accepted, rejected, preserved, correct, belowFloor, wrongSession));
        }
        var samples = new List<BenchmarkSample>(); var benchmark = new List<BenchmarkRow>();
        var root = Path.Combine(Path.GetTempPath(), "digital-twin-benchmark-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var samplesCount in new[] { 0, 1024, 16384, 262144 }) foreach (var kind in new[] { "Memory", "File" })
            {
                var start = samples.Count; var state = TwinData.Create(p.Seed, samplesCount); var key = Wire.ResearchKey(p.Seed); var bytes = Wire.StateBytes(state).Length;
                var source = new TrustedSource("TWIN", state, key); IRecordStore store = kind == "Memory" ? new MemoryRecordStore(LocalRecord.Initial(state)) : new FileRecordStore(Path.Combine(root, $"{samplesCount}.json"), LocalRecord.Initial(state));
                var agent = new RecoveryAgent("TWIN", store, key, RecoveryProtection.Full, new MonotonicClock(), timeout: 60000);
                for (var trial = -3; trial < p.BenchmarkRepeats; trial++)
                {
                    ct.ThrowIfCancellationRequested(); source.Update(new Delta(Pressure: state.Pressure + trial + 5)); var request = agent.Begin();
                    var begin = System.Diagnostics.Stopwatch.GetTimestamp(); var checkpoint = source.Checkpoint(request); var createMs = System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    begin = System.Diagnostics.Stopwatch.GetTimestamp(); var decision = agent.Receive(checkpoint); var commitMs = System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    Check.That(decision == "Accepted" && agent.Record.Version == source.Read().Version && agent.Record.IsConsistent, "benchmark restores complete version");
                    if (trial >= 0) samples.Add(new(kind, bytes, trial, createMs, commitMs));
                }
                var slice = samples.Skip(start).ToArray(); benchmark.Add(new(kind, bytes, Summary.Of(slice.Select(x => x.SnapshotCreationMs)), Summary.Of(slice.Select(x => x.VerificationAndCommitMs))));
            }
        }
        finally { Directory.Delete(root, true); }
        return new("DigitalTwinRecovery", "763aae5ee00912d2c4c484dfc3d931b2cfbfcb3c", p, rows, benchmark, samples,
            ["Recovery starts from an established discrepancy; detection is not part of this experiment.", "Snapshot data is compared with the captured source version, not with a continuously changing source at arbitrary later time.",
             "Benchmark measures local snapshot creation, verification and commit on this machine; network RTT and transfer are excluded.", "File store is single-writer and tested for process crashes on Linux; power loss, hostile storage rollback and multiple writers are not covered.",
             "Known version floor is trusted input from an authenticated freshness check; the source's persistent epoch management is outside scope."]);
    }
}
