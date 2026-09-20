using System.Text.Json;

namespace MarineMonitor.Core;

public sealed record SignalData
{
    public string FileName { get; init; } = "";
    public DateTime RecordedAt { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double SampleRate { get; init; }
    public double[] Values { get; init; } = [];
    public double[] FileRms { get; init; } = [];
    public bool Completed { get; init; }
}
public sealed record EquipmentData
{
    public string EquipmentId { get; init; } = "";
    public string SourceEquipmentId { get; init; } = "";
    public string SimulationLabel { get; init; } = "";
    public string Diagnosis { get; init; } = "";
    public string State { get; init; } = "";
    public DateTime PublishedAtUtc { get; init; }
    public double ReplaySeconds { get; init; }
    public SignalData? Current { get; init; }
    public SignalData? Vibration { get; init; }
    public string Error { get; init; } = "";
}
public sealed record Telemetry
{
    public int Version { get; init; }
    public string Type { get; init; } = "";
    public long Sequence { get; init; }
    public DateTime SentAtUtc { get; init; }
    public EquipmentData[] Equipment { get; init; } = [];
    public static Telemetry Parse(string json)
    {
        var data = JsonSerializer.Deserialize<Telemetry>(json, JsonOptions) ?? throw new JsonException("Empty telemetry");
        if (data.Version != 1 || data.Type != "telemetry" || data.Equipment == null || data.Equipment.Length > 32)
            throw new JsonException("Invalid telemetry envelope");
        var ids = new HashSet<string>();
        foreach (var e in data.Equipment)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.EquipmentId) || !ids.Add(e.EquipmentId) || !double.IsFinite(e.ReplaySeconds))
                throw new JsonException("Invalid equipment identity");
            if (e.State is not ("Playing" or "Paused" or "Stopped" or "Completed" or "Error")) throw new JsonException("Invalid equipment state");
            ValidateSignal(e.Current, 3); ValidateSignal(e.Vibration, 1);
        }
        return data;
    }
    private static void ValidateSignal(SignalData? signal, int channels)
    {
        if (signal == null) return;
        if (signal.Values == null || signal.FileRms == null || signal.Values.Length != channels || signal.FileRms.Length != channels ||
            signal.Values.Any(x => !double.IsFinite(x)) || signal.FileRms.Any(x => !double.IsFinite(x)) ||
            !double.IsFinite(signal.SampleRate) || signal.SampleRate <= 0 || !double.IsFinite(signal.PositionSeconds) ||
            !double.IsFinite(signal.DurationSeconds) || signal.DurationSeconds <= 0 || signal.PositionSeconds < 0 || signal.PositionSeconds >= signal.DurationSeconds)
            throw new JsonException("Invalid signal data");
    }
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
}
public sealed record ReceivedTelemetry(Telemetry Data, DateTime ReceivedAtUtc, long ConnectionId);
public sealed record ClientEvent(DateTime Time, string Message);
public sealed record CommandResult(bool Ok, string Code, string Message, string CommandId);
