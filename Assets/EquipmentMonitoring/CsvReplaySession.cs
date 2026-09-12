using System;
using System.IO;

namespace ShipRobot.EquipmentMonitoring
{
    // Pure C# playback engine, usable and testable without Unity or a robot controller.
    public sealed class CsvReplaySession : IEquipmentDataSource
    {
        private sealed class Stream
        {
            private readonly string[] paths;
            private readonly string equipment, label;
            private readonly int channels;
            private int index;
            private double position;
            private CsvSignal file;
            public bool Completed { get; private set; }
            public Stream(string folder, int channels, string equipment, string label)
            {
                paths = Directory.GetFiles(folder, "*.csv");
                Array.Sort(paths, StringComparer.Ordinal);
                if (paths.Length == 0) throw new InvalidDataException("No CSV files: " + folder);
                this.channels = channels; this.equipment = equipment; this.label = label;
                file = CsvSignal.Load(paths[0], channels, equipment, label);
            }
            public void Advance(double delta, bool loop)
            {
                if (Completed) return;
                position += delta;
                while (position >= file.Count / file.SampleRate)
                {
                    position -= file.Count / file.SampleRate;
                    if (++index >= paths.Length)
                    {
                        if (!loop) { Completed = true; position = (file.Count - 1) / file.SampleRate; return; }
                        index = 0;
                    }
                    file = CsvSignal.Load(paths[index], channels, equipment, label);
                }
            }
            public SignalSnapshot Snapshot() => new SignalSnapshot(file,
                Math.Min(file.Count - 1, (int)(position * file.SampleRate)), Completed);
        }

        private Stream current, vibration;
        private readonly string id;
        private string equipment = "", label = "";
        private double elapsed;
        private bool loop;
        private DataSourceState state = DataSourceState.Stopped;
        private string error = "";
        public EquipmentSnapshot Latest { get; private set; }
        public event Action<EquipmentSnapshot> SnapshotChanged;
        public CsvReplaySession(string equipmentId) { id = equipmentId; Publish(); }

        public void Start(string root, string capacity, string sourceEquipment, string simulationLabel, bool repeat)
        {
            current = vibration = null; elapsed = 0; error = "";
            equipment = sourceEquipment; label = simulationLabel; loop = repeat;
            try
            {
                ValidateSegment(capacity); ValidateSegment(equipment); ValidateSegment(label);
                var nextCurrent = new Stream(Path.Combine(root, "current", capacity, equipment, label), 3, equipment, label);
                var nextVibration = new Stream(Path.Combine(root, "vibration", capacity, equipment, label), 1, equipment, label);
                current = nextCurrent; vibration = nextVibration; state = DataSourceState.Playing;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { Fault(ex); }
            Publish();
        }
        private static void ValidateSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new ArgumentException("Invalid dataset path segment");
        }
        public void Tick(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (state != DataSourceState.Playing) return;
            try
            {
                current.Advance(seconds, loop); vibration.Advance(seconds, loop); elapsed += seconds;
                if (current.Completed && vibration.Completed) state = DataSourceState.Completed;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { Fault(ex); }
            Publish();
        }
        public void Pause() { if (state == DataSourceState.Playing) { state = DataSourceState.Paused; Publish(); } }
        public void Resume() { if (state == DataSourceState.Paused) { state = DataSourceState.Playing; Publish(); } }
        public void Stop() { current = vibration = null; state = DataSourceState.Stopped; elapsed = 0; error = ""; Publish(); }
        private void Fault(Exception ex) { current = vibration = null; state = DataSourceState.Error; error = ex.Message; }
        private void Publish()
        {
            Latest = new EquipmentSnapshot(id, equipment, label, state, elapsed, current?.Snapshot(), vibration?.Snapshot(), error);
            SnapshotChanged?.Invoke(Latest);
        }
    }
}
