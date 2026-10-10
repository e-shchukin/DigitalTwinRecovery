using System.Globalization;
using System.Text;
namespace DigitalTwinResearch;
public static class Export
{
    private static string N(double? x) => x?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    public static void Csv(RecoveryReport report, string directory)
    {

        var functional = new StringBuilder("scenario,mode,runs,accepted,rejected,state_preserved_on_rejection,correct_after_accepted,below_known_floor,wrong_session_accepted\n");
        foreach (var x in report.Functional) functional.AppendLine($"{x.Scenario},{x.Mode},{x.Runs},{x.Accepted},{x.Rejected},{x.StatePreservedOnRejection},{x.CorrectAfterAccepted},{x.BelowKnownFloor},{x.WrongSessionAccepted}");
        var samples = new StringBuilder("store,state_bytes,trial,snapshot_creation_ms,verification_and_commit_ms\n");
        foreach (var x in report.Samples) samples.AppendLine($"{x.Store},{x.StateBytes},{x.Trial},{N(x.SnapshotCreationMs)},{N(x.VerificationAndCommitMs)}");
        var summary = new StringBuilder("store,state_bytes,creation_mean_ms,creation_p95_ms,verification_commit_mean_ms,verification_commit_p95_ms\n");
        foreach (var x in report.Benchmark) summary.AppendLine($"{x.Store},{x.StateBytes},{N(x.SnapshotCreation.MeanMs)},{N(x.SnapshotCreation.P95Ms)},{N(x.VerificationAndCommit.MeanMs)},{N(x.VerificationAndCommit.P95Ms)}");

        File.WriteAllText(Path.Combine(directory, "functional.csv"), functional.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "samples.csv"), samples.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "summary.csv"), summary.ToString(), Encoding.UTF8);
    }
}
