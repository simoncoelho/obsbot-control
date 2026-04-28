using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Obsbot.Motion;

public static class MotionRunLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task WriteRunAsync(MotionRun run, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var safeName = string.Join("_", run.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var stem = $"{run.StartedAtUtc:yyyyMMdd_HHmmss_fff}_{safeName}";

        var jsonPath = Path.Combine(directory, stem + ".json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(run, JsonOptions), cancellationToken);

        var csvPath = Path.Combine(directory, stem + ".csv");
        await File.WriteAllTextAsync(csvPath, ToCsv(run), cancellationToken);
    }

    public static async Task WriteSummaryAsync(IEnumerable<MotionRun> runs, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "summary.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(runs, JsonOptions), cancellationToken);
    }

    private static string ToCsv(MotionRun run)
    {
        var builder = new StringBuilder();
        builder.AppendLine("elapsed_ms,pan,tilt,pan_error,tilt_error,pan_step,tilt_step,event");
        foreach (var sample in run.Samples)
        {
            builder
                .Append(sample.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.Pan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(sample.Tilt?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(sample.PanError.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.TiltError.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.PanStep.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.TiltStep.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append('"').Append(sample.Event.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"')
                .AppendLine();
        }

        return builder.ToString();
    }
}
