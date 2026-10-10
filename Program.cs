using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalTwinResearch;

public static class Program
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--crash-worker") { SelfTests.CrashWorker(args[1], Enum.Parse<CommitPoint>(args[2])); return 74; }

            if (args.Contains("--self-test")) { SelfTests.Run(); return 0; }
            if (args.Contains("--run"))
            {
                var runs = IntArgument(args, "--runs", 1000); var seed = IntArgument(args, "--seed", 20260809);
                var parameters = new RecoveryParameters(Runs: runs, Seed: seed);
                var report = RecoveryExperiment.Run(parameters);
                var directory = Path.GetFullPath(StringArgument(args, "--output", "results.local")); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { report, environment = EnvironmentInfo() }, Json), Encoding.UTF8);
                Export.Csv(report, directory);
                Console.WriteLine("Written: " + directory); return 0;
            }
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            var app = builder.Build();
            if (!args.Contains("--urls")) app.Urls.Add("http://localhost:5082");
            app.UseDefaultFiles(); app.UseStaticFiles();
            app.MapGet("/health", () => Results.Ok(new { status = "ok", project = "DigitalTwinRecovery" }));
            app.MapGet("/api/defaults", () => new RecoveryParameters());
            app.MapPost("/api/experiment", async (RecoveryParameters p, CancellationToken cancellation) =>
            {
                try { return (IResult)Results.Ok(new { report = await Task.Run(() => RecoveryExperiment.Run(p, cancellation), cancellation), environment = EnvironmentInfo() }); }
                catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            });
            await app.RunAsync(); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
    }
    private static int IntArgument(string[] args, string option, int fallback) => int.Parse(StringArgument(args, option, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    private static string StringArgument(string[] args, string option, string fallback)
    {
        var index = Array.IndexOf(args, option); if (index < 0) return fallback;
        if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + option); return args[index + 1];
    }
    public static object EnvironmentInfo() => new
    {
        generatedAtUtc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
        assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))),
        upstreamCommit = "763aae5ee00912d2c4c484dfc3d931b2cfbfcb3c"
    };
}
