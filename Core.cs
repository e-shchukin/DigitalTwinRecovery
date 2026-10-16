using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace DigitalTwinResearch;

public readonly record struct VersionStamp(int Epoch, long Sequence) : IComparable<VersionStamp>
{
    public int CompareTo(VersionStamp other) => Epoch != other.Epoch ? Epoch.CompareTo(other.Epoch) : Sequence.CompareTo(other.Sequence);
    public static VersionStamp Max(VersionStamp a, VersionStamp b) => a.CompareTo(b) >= 0 ? a : b;
    public bool IsValid => Epoch >= 1 && Sequence >= 0;
}

public sealed record TwinData(int Pressure, int Temperature, bool ValveOpen, int FlowRate, ImmutableArray<int> Samples)
{
    public static TwinData Create(int seed, int sampleCount = 0)
    {
        var r = new Random(seed);
        return new TwinData(r.Next(90, 111), r.Next(60, 91), r.Next(2) == 1, r.Next(30, 71),
            Enumerable.Range(0, sampleCount).Select(_ => r.Next()).ToImmutableArray());
    }
}

public sealed record Delta(int? Pressure = null, int? Temperature = null, bool? ValveOpen = null, int? FlowRate = null)
{
    public TwinData Apply(TwinData data) => data with { Pressure = Pressure ?? data.Pressure, Temperature = Temperature ?? data.Temperature,
        ValveOpen = ValveOpen ?? data.ValveOpen, FlowRate = FlowRate ?? data.FlowRate };
}

public sealed record LocalRecord(TwinData State, VersionStamp Version, string Hash, VersionStamp MinimumVersion)
{
    public bool IsConsistent => Version.IsValid && MinimumVersion.IsValid && MinimumVersion.CompareTo(Version) >= 0 && Wire.Hash(State) == Hash;
    public static LocalRecord Initial(TwinData state) => new(state, new(1, 0), Wire.Hash(state), new(1, 0));
}

public sealed record Packet(string Type, string TwinId, VersionStamp Version, string Nonce, byte[] Payload, string Hash, string Tag);

public static class Wire
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;
    public static byte[] StateBytes(TwinData s)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write(s.Pressure); w.Write(s.Temperature); w.Write(s.ValveOpen); w.Write(s.FlowRate);
        var samples = s.Samples.IsDefault ? ImmutableArray<int>.Empty : s.Samples;
        w.Write(samples.Length); foreach (var value in samples) w.Write(value);
        return stream.ToArray();
    }
    public static TwinData ReadState(byte[] bytes)
    {
        if (bytes.Length > MaxPayloadBytes) throw new InvalidDataException("State exceeds size limit.");
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        var p = r.ReadInt32(); var t = r.ReadInt32(); var valveByte = r.ReadByte(); var f = r.ReadInt32(); var n = r.ReadInt32();
        if (valveByte > 1 || n < 0 || n > (MaxPayloadBytes - 17) / 4 || stream.Length - stream.Position != 4L * n)
            throw new InvalidDataException("Malformed state.");
        var values = ImmutableArray.CreateBuilder<int>(n); for (var i = 0; i < n; i++) values.Add(r.ReadInt32());
        return new(p, t, valveByte == 1, f, values.MoveToImmutable());
    }
    public static byte[] DeltaBytes(Delta d)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        var mask = (d.Pressure.HasValue ? 1 : 0) | (d.Temperature.HasValue ? 2 : 0) | (d.ValveOpen.HasValue ? 4 : 0) | (d.FlowRate.HasValue ? 8 : 0);
        w.Write((byte)mask); if (d.Pressure.HasValue) w.Write(d.Pressure.Value); if (d.Temperature.HasValue) w.Write(d.Temperature.Value);
        if (d.ValveOpen.HasValue) w.Write(d.ValveOpen.Value); if (d.FlowRate.HasValue) w.Write(d.FlowRate.Value);
        return stream.ToArray();
    }
    public static Delta ReadDelta(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        var mask = r.ReadByte(); if (mask > 15) throw new InvalidDataException("Malformed delta.");
        int? p = (mask & 1) != 0 ? r.ReadInt32() : null; int? t = (mask & 2) != 0 ? r.ReadInt32() : null;
        bool? valve = null; if ((mask & 4) != 0) { var b = r.ReadByte(); if (b > 1) throw new InvalidDataException("Malformed bool."); valve = b == 1; }
        int? f = (mask & 8) != 0 ? r.ReadInt32() : null;
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing delta bytes.");
        return new(p, t, valve, f);
    }
    public static string Hash(TwinData data) => Convert.ToHexString(SHA256.HashData(StateBytes(data)));
    private static byte[] Canon(Packet p)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write("DigitalTwinResearch/v2"); w.Write(p.Type); w.Write(p.TwinId); w.Write(p.Version.Epoch); w.Write(p.Version.Sequence);
        w.Write(p.Nonce); w.Write(p.Payload.Length); w.Write(p.Payload); w.Write(p.Hash); return stream.ToArray();
    }
    public static Packet Sign(Packet p, byte[] key) => p with { Tag = Convert.ToHexString(HMACSHA256.HashData(key, Canon(p))) };
    public static bool Verify(Packet? p, string expectedType, string twinId, byte[] key)
    {
        if (p is null || p.Type != expectedType || p.TwinId != twinId || !p.Version.IsValid || p.Nonce is null || p.Nonce.Length > 128 ||
            p.Payload is null || p.Payload.Length > MaxPayloadBytes || p.Hash is null || p.Hash.Length != 64 || p.Tag is null || p.Tag.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, Canon(p)), Convert.FromHexString(p.Tag)); }
        catch (FormatException) { return false; }
    }
    public static Packet Control(string type, string id, VersionStamp version, string nonce, string hash, byte[] key) =>
        Sign(new(type, id, version, nonce, Array.Empty<byte>(), hash, ""), key);
    public static byte[] ResearchKey(int seed) => SHA256.HashData(Encoding.UTF8.GetBytes($"research-only/{seed}"));
}

public sealed class TrustedSource
{
    private readonly object _gate = new(); private LocalRecord _record; private readonly byte[] _key;
    public string TwinId { get; }
    public TrustedSource(string id, TwinData state, byte[] key) { TwinId = id; _record = LocalRecord.Initial(state); _key = key.ToArray(); }
    public LocalRecord Read() { lock (_gate) return _record; }
    public Packet Update(Delta delta)
    {
        lock (_gate)
        {
            var state = delta.Apply(_record.State); var version = _record.Version with { Sequence = checked(_record.Version.Sequence + 1) };
            _record = new(state, version, Wire.Hash(state), version);
            return Wire.Sign(new("UPDATE", TwinId, version, "", Wire.DeltaBytes(delta), _record.Hash, ""), _key);
        }
    }
    public void NewEpoch(TwinData state)
    {
        lock (_gate) { var version = new VersionStamp(checked(_record.Version.Epoch + 1), 0); _record = new(state, version, Wire.Hash(state), version); }
    }
    public Packet Status(Packet probe)
    {
        if (!Wire.Verify(probe, "SYNC_PROBE", TwinId, _key) || probe.Payload.Length != 0 || probe.Nonce.Length == 0)
            throw new InvalidDataException("Invalid SYNC_PROBE.");
        lock (_gate) return Wire.Control("SYNC_STATUS", TwinId, _record.Version, probe.Nonce, _record.Hash, _key);
    }
    public Packet Checkpoint(Packet request)
    {
        if (!Wire.Verify(request, "RESYNC_REQUEST", TwinId, _key) || request.Payload.Length != 0 || request.Nonce.Length == 0)
            throw new InvalidDataException("Invalid RESYNC_REQUEST.");
        lock (_gate) return Wire.Sign(new("CHECKPOINT", TwinId, _record.Version, request.Nonce, Wire.StateBytes(_record.State), _record.Hash, ""), _key);
    }
}

public interface IClock { double NowMs { get; } }
public sealed class ManualClock : IClock
{
    public double NowMs { get; private set; }
    public void MoveTo(double time) { if (!double.IsFinite(time) || time < NowMs) throw new ArgumentOutOfRangeException(nameof(time)); NowMs = time; }
}
public sealed class MonotonicClock : IClock
{
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
    public double NowMs => System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
}
public interface INonceSource { string Next(); }
public sealed class CryptoNonce : INonceSource { public string Next() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); }
public sealed class ResearchNonce(string label) : INonceSource
{
    private long _counter;
    public string Next() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{label}/{Interlocked.Increment(ref _counter)}")))[..32];
}

public sealed class EventQueue(ManualClock clock)
{
    private readonly PriorityQueue<Action, (double Time, long Order)> _queue = new(); private long _order;
    public void At(double time, Action action) { if (!double.IsFinite(time) || time < clock.NowMs) throw new ArgumentOutOfRangeException(nameof(time)); _queue.Enqueue(action, (time, _order++)); }
    public void RunUntil(double end, CancellationToken cancellation = default)
    {
        var steps = 0;
        while (_queue.TryPeek(out _, out var priority) && priority.Time <= end)
        {
            cancellation.ThrowIfCancellationRequested(); if (++steps > 100_000) throw new InvalidOperationException("Event budget exceeded.");
            var action = _queue.Dequeue(); clock.MoveTo(priority.Time); action();
        }
        clock.MoveTo(end);
    }
}

public sealed record Summary(int Samples, double? MeanMs, double? MedianMs, double? P95Ms, double? MaxMs)
{
    public static Summary Of(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray(); if (values.Length == 0) return new(0, null, null, null, null);
        var n = values.Length; return new(n, values.Average(), n % 2 == 0 ? (values[n / 2 - 1] + values[n / 2]) / 2 : values[n / 2], values[(int)Math.Ceiling(n * .95) - 1], values[^1]);
    }
}

public static class Check
{
    private static int _count;
    public static int Count => _count;
    public static void That(bool condition, string description) { if (!condition) throw new InvalidOperationException("FAILED: " + description); Interlocked.Increment(ref _count); }
    public static bool SameState(TwinData a, TwinData b) => Wire.StateBytes(a).AsSpan().SequenceEqual(Wire.StateBytes(b));
}
