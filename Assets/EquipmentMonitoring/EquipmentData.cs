using System;
using System.Collections.Generic;

namespace ShipRobot.EquipmentMonitoring
{
    public enum DataSourceState { Stopped, Playing, Paused, Completed, Error }

    // Transport-neutral contract: neither consumers nor future network adapters need CSV knowledge.
    public interface IEquipmentDataSource
    {
        EquipmentSnapshot Latest { get; }
        event Action<EquipmentSnapshot> SnapshotChanged;
    }

    public sealed class SignalSnapshot
    {
        public string FileName { get; }
        public DateTime RecordedAt { get; } // Source local timestamp; timezone is unspecified.
        public double PositionSeconds { get; }
        public double DurationSeconds { get; }
        public double SampleRate { get; }
        public IReadOnlyList<double> Values { get; }
        public IReadOnlyList<double> FileRms { get; }
        public bool Completed { get; }

        internal SignalSnapshot(CsvSignal file, int index, bool completed)
        {
            FileName = System.IO.Path.GetFileName(file.Path);
            RecordedAt = file.RecordedAt;
            PositionSeconds = index / file.SampleRate;
            DurationSeconds = file.Count / file.SampleRate;
            SampleRate = file.SampleRate;
            var values = new double[file.Channels];
            Array.Copy(file.Samples, index * file.Channels, values, 0, values.Length);
            Values = Array.AsReadOnly(values);
            FileRms = Array.AsReadOnly((double[])file.Rms.Clone());
            Completed = completed;
        }
    }

    public sealed class EquipmentSnapshot
    {
        public string EquipmentId { get; }
        public string SourceEquipmentId { get; }
        public string SimulationLabel { get; }
        public string Diagnosis => "NotEvaluated";
        public DataSourceState State { get; }
        public DateTime PublishedAtUtc { get; }
        public double ReplaySeconds { get; }
        public SignalSnapshot Current { get; }
        public SignalSnapshot Vibration { get; }
        public string Error { get; }

        internal EquipmentSnapshot(string id, string source, string label, DataSourceState state,
            double seconds, SignalSnapshot current, SignalSnapshot vibration, string error)
        {
            EquipmentId = id; SourceEquipmentId = source; SimulationLabel = label;
            State = state; ReplaySeconds = seconds; Current = current; Vibration = vibration;
            Error = error; PublishedAtUtc = DateTime.UtcNow;
        }
    }
}
