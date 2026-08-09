using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DigitalTwinSync.Experiment;

public sealed record TwinState(
    int Pressure,
    int Temperature,
    bool ValveOpen,
    int FlowRate);

public sealed record StateDelta(
    int? Pressure = null,
    int? Temperature = null,
    bool? ValveOpen = null,
    int? FlowRate = null)
{
    public TwinState Apply(TwinState state)
    {
        return state with
        {
            Pressure = Pressure ?? state.Pressure,
            Temperature = Temperature ?? state.Temperature,
            ValveOpen = ValveOpen ?? state.ValveOpen,
            FlowRate = FlowRate ?? state.FlowRate
        };
    }
}

public sealed record UpdateMessage(
    string Type,
    string TwinId,
    int Epoch,
    long Sequence,
    StateDelta Delta,
    string ResultHash,
    string Tag);

public sealed record ProbeMessage(
    string Type,
    string TwinId,
    string Nonce,
    int LocalEpoch,
    long LocalSequence,
    string LocalHash,
    string Tag);

public sealed record StatusMessage(
    string Type,
    string TwinId,
    string Nonce,
    int SourceEpoch,
    long SourceSequence,
    string SourceHash,
    string Tag);

public sealed record ResyncRequest(
    string Type,
    string TwinId,
    string Nonce,
    int LocalEpoch,
    long LocalSequence,
    string LocalHash,
    string Tag);

public sealed record CheckpointMessage(
    string Type,
    string TwinId,
    string Nonce,
    int Epoch,
    long Sequence,
    TwinState State,
    string ResultHash,
    string Tag);

public enum UpdateAcceptance
{
    Accepted,
    AuthenticationFailed,
    SequenceMismatch,
    ResultHashMismatch
}

public enum StatusEvaluation
{
    Synchronized,
    Desynchronized,
    Rejected
}

public enum CheckpointAcceptance
{
    Accepted,
    AuthenticationFailed,
    NonceMismatch,
    RollbackRejected,
    ResultHashMismatch
}

public static class ProtocolCanonicalizer
{
    public static string State(TwinState state)
    {
        return string.Join('|',
            state.Pressure.ToString(CultureInfo.InvariantCulture),
            state.Temperature.ToString(CultureInfo.InvariantCulture),
            state.ValveOpen ? "1" : "0",
            state.FlowRate.ToString(CultureInfo.InvariantCulture));
    }

    public static string Delta(StateDelta delta)
    {
        return string.Join('|',
            Integer(delta.Pressure),
            Integer(delta.Temperature),
            delta.ValveOpen is null ? "_" : delta.ValveOpen.Value ? "1" : "0",
            Integer(delta.FlowRate));
    }

    public static string Update(
        string type,
        string twinId,
        int epoch,
        long sequence,
        StateDelta delta,
        string resultHash)
    {
        return string.Join(';', type, twinId, epoch, sequence, Delta(delta), resultHash);
    }

    public static string Probe(
        string type,
        string twinId,
        string nonce,
        int epoch,
        long sequence,
        string hash)
    {
        return string.Join(';', type, twinId, nonce, epoch, sequence, hash);
    }

    public static string Status(
        string type,
        string twinId,
        string nonce,
        int epoch,
        long sequence,
        string hash)
    {
        return string.Join(';', type, twinId, nonce, epoch, sequence, hash);
    }

    public static string Resync(
        string type,
        string twinId,
        string nonce,
        int epoch,
        long sequence,
        string hash)
    {
        return string.Join(';', type, twinId, nonce, epoch, sequence, hash);
    }

    public static string Checkpoint(
        string type,
        string twinId,
        string nonce,
        int epoch,
        long sequence,
        TwinState state,
        string resultHash)
    {
        return string.Join(';', type, twinId, nonce, epoch, sequence, State(state), resultHash);
    }

    private static string Integer(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "_";
    }
}

public static class ProtocolCrypto
{
    public static string HashState(TwinState state)
    {
        var bytes = Encoding.UTF8.GetBytes(ProtocolCanonicalizer.State(state));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string Sign(string payload, byte[] key)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return Convert.ToHexString(HMACSHA256.HashData(key, bytes));
    }

    public static bool Verify(string payload, string tag, byte[] key)
    {
        try
        {
            var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
            var actual = Convert.FromHexString(tag);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class SourceSimulator
{
    private readonly byte[] _key;

    public SourceSimulator(string twinId, TwinState initialState, byte[] key)
    {
        TwinId = twinId;
        State = initialState;
        _key = key.ToArray();
    }

    public string TwinId { get; }
    public TwinState State { get; private set; }
    public int Epoch { get; private set; } = 1;
    public long Sequence { get; private set; }

    public UpdateMessage CreateUpdate(StateDelta delta)
    {
        State = delta.Apply(State);
        Sequence++;
        var resultHash = ProtocolCrypto.HashState(State);
        var payload = ProtocolCanonicalizer.Update(
            "UPDATE",
            TwinId,
            Epoch,
            Sequence,
            delta,
            resultHash);

        return new UpdateMessage(
            "UPDATE",
            TwinId,
            Epoch,
            Sequence,
            delta,
            resultHash,
            ProtocolCrypto.Sign(payload, _key));
    }

    public StatusMessage CreateStatus(ProbeMessage probe)
    {
        var probePayload = ProtocolCanonicalizer.Probe(
            probe.Type,
            probe.TwinId,
            probe.Nonce,
            probe.LocalEpoch,
            probe.LocalSequence,
            probe.LocalHash);

        if (probe.TwinId != TwinId || !ProtocolCrypto.Verify(probePayload, probe.Tag, _key))
        {
            throw new InvalidOperationException("Источник отклонил неаутентифицированный SYNC_PROBE.");
        }

        var hash = ProtocolCrypto.HashState(State);
        var payload = ProtocolCanonicalizer.Status(
            "SYNC_STATUS",
            TwinId,
            probe.Nonce,
            Epoch,
            Sequence,
            hash);

        return new StatusMessage(
            "SYNC_STATUS",
            TwinId,
            probe.Nonce,
            Epoch,
            Sequence,
            hash,
            ProtocolCrypto.Sign(payload, _key));
    }

    public CheckpointMessage CreateCheckpoint(ResyncRequest request)
    {
        var requestPayload = ProtocolCanonicalizer.Resync(
            request.Type,
            request.TwinId,
            request.Nonce,
            request.LocalEpoch,
            request.LocalSequence,
            request.LocalHash);

        if (request.TwinId != TwinId || !ProtocolCrypto.Verify(requestPayload, request.Tag, _key))
        {
            throw new InvalidOperationException("Источник отклонил неаутентифицированный RESYNC_REQUEST.");
        }

        var resultHash = ProtocolCrypto.HashState(State);
        var payload = ProtocolCanonicalizer.Checkpoint(
            "CHECKPOINT",
            TwinId,
            request.Nonce,
            Epoch,
            Sequence,
            State,
            resultHash);

        return new CheckpointMessage(
            "CHECKPOINT",
            TwinId,
            request.Nonce,
            Epoch,
            Sequence,
            State,
            resultHash,
            ProtocolCrypto.Sign(payload, _key));
    }
}

public sealed class TwinSimulator
{
    private readonly byte[] _key;

    public TwinSimulator(string twinId, TwinState initialState, byte[] key)
    {
        TwinId = twinId;
        State = initialState;
        _key = key.ToArray();
    }

    public string TwinId { get; }
    public TwinState State { get; private set; }
    public int Epoch { get; private set; } = 1;
    public long Sequence { get; private set; }

    public UpdateAcceptance ReceiveUpdate(UpdateMessage message, ProtectionMode mode)
    {
        var payload = ProtocolCanonicalizer.Update(
            message.Type,
            message.TwinId,
            message.Epoch,
            message.Sequence,
            message.Delta,
            message.ResultHash);

        if (message.TwinId != TwinId || !ProtocolCrypto.Verify(payload, message.Tag, _key))
        {
            return UpdateAcceptance.AuthenticationFailed;
        }

        if (mode is ProtectionMode.HmacAndSequence or ProtectionMode.ProposedMethod)
        {
            if (message.Epoch != Epoch || message.Sequence != Sequence + 1)
            {
                return UpdateAcceptance.SequenceMismatch;
            }
        }

        var candidate = message.Delta.Apply(State);

        if (mode == ProtectionMode.ProposedMethod &&
            !string.Equals(
                ProtocolCrypto.HashState(candidate),
                message.ResultHash,
                StringComparison.Ordinal))
        {
            return UpdateAcceptance.ResultHashMismatch;
        }

        State = candidate;
        Epoch = message.Epoch;
        Sequence = message.Sequence;
        return UpdateAcceptance.Accepted;
    }

    public ProbeMessage CreateProbe(string nonce)
    {
        var hash = ProtocolCrypto.HashState(State);
        var payload = ProtocolCanonicalizer.Probe(
            "SYNC_PROBE",
            TwinId,
            nonce,
            Epoch,
            Sequence,
            hash);

        return new ProbeMessage(
            "SYNC_PROBE",
            TwinId,
            nonce,
            Epoch,
            Sequence,
            hash,
            ProtocolCrypto.Sign(payload, _key));
    }

    public StatusEvaluation EvaluateStatus(StatusMessage status, string expectedNonce)
    {
        var payload = ProtocolCanonicalizer.Status(
            status.Type,
            status.TwinId,
            status.Nonce,
            status.SourceEpoch,
            status.SourceSequence,
            status.SourceHash);

        if (status.TwinId != TwinId ||
            status.Nonce != expectedNonce ||
            !ProtocolCrypto.Verify(payload, status.Tag, _key))
        {
            return StatusEvaluation.Rejected;
        }

        var localHash = ProtocolCrypto.HashState(State);
        return status.SourceEpoch == Epoch &&
               status.SourceSequence == Sequence &&
               string.Equals(status.SourceHash, localHash, StringComparison.Ordinal)
            ? StatusEvaluation.Synchronized
            : StatusEvaluation.Desynchronized;
    }

    public ResyncRequest CreateResyncRequest(string nonce)
    {
        var hash = ProtocolCrypto.HashState(State);
        var payload = ProtocolCanonicalizer.Resync(
            "RESYNC_REQUEST",
            TwinId,
            nonce,
            Epoch,
            Sequence,
            hash);

        return new ResyncRequest(
            "RESYNC_REQUEST",
            TwinId,
            nonce,
            Epoch,
            Sequence,
            hash,
            ProtocolCrypto.Sign(payload, _key));
    }

    public CheckpointAcceptance ReceiveCheckpoint(
        CheckpointMessage checkpoint,
        string expectedNonce,
        ProtectionMode mode)
    {
        var payload = ProtocolCanonicalizer.Checkpoint(
            checkpoint.Type,
            checkpoint.TwinId,
            checkpoint.Nonce,
            checkpoint.Epoch,
            checkpoint.Sequence,
            checkpoint.State,
            checkpoint.ResultHash);

        if (checkpoint.TwinId != TwinId ||
            !ProtocolCrypto.Verify(payload, checkpoint.Tag, _key))
        {
            return CheckpointAcceptance.AuthenticationFailed;
        }

        if (mode == ProtectionMode.ProposedMethod &&
            checkpoint.Nonce != expectedNonce)
        {
            return CheckpointAcceptance.NonceMismatch;
        }

        if ((mode is ProtectionMode.HmacAndSequence or ProtectionMode.ProposedMethod) &&
            (checkpoint.Epoch < Epoch ||
             checkpoint.Epoch == Epoch && checkpoint.Sequence < Sequence))
        {
            return CheckpointAcceptance.RollbackRejected;
        }

        if (mode == ProtectionMode.ProposedMethod &&
            !string.Equals(
                ProtocolCrypto.HashState(checkpoint.State),
                checkpoint.ResultHash,
                StringComparison.Ordinal))
        {
            return CheckpointAcceptance.ResultHashMismatch;
        }

        State = checkpoint.State;
        Epoch = checkpoint.Epoch;
        Sequence = checkpoint.Sequence;
        return CheckpointAcceptance.Accepted;
    }

    public void CorruptState(TwinState corruptedState)
    {
        State = corruptedState;
    }
}
