using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ShipRobot.EquipmentMonitoring
{
    // This dataset has nine metadata records followed by time + numeric channels.
    public sealed class CsvSignal
    {
        public string Path { get; private set; }
        public DateTime RecordedAt { get; private set; }
        public double SampleRate { get; private set; }
        public int Count { get; private set; }
        public int Channels { get; private set; }
        internal double[] Samples;
        internal double[] Rms;

        public static CsvSignal Load(string path, int channels, string equipmentId, string label)
        {
            if (channels != 1 && channels != 3) throw new ArgumentOutOfRangeException(nameof(channels));
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
            string[] Read(string key)
            {
                string line = reader.ReadLine() ?? throw new InvalidDataException("Missing " + key);
                string[] cells = line.Split(',');
                if (cells.Length < 2 || cells[0].Trim().Replace('_', ' ') != key)
                    throw new InvalidDataException("Expected metadata: " + key);
                return cells;
            }
            double Number(string value)
            {
                if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    || double.IsNaN(n) || double.IsInfinity(n))
                    throw new InvalidDataException("Invalid finite number: " + value);
                return n;
            }
            var file = new CsvSignal { Path = path, Channels = channels };
            file.RecordedAt = DateTime.SpecifyKind(DateTime.ParseExact(Read("Date")[1].Trim(),
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
            Read("Filename");
            if (Read("Data Label")[1].Trim() != label) throw new InvalidDataException("Label does not match folder");
            Read("Label No");
            if (Read("Motor Spec")[1].Trim() != equipmentId) throw new InvalidDataException("Equipment does not match folder");
            string period = Read("Period")[1].Trim();
            if (!period.EndsWith("SEC", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported period unit");
            double duration = Number(period.Substring(0, period.Length - 3));
            file.SampleRate = Number(Read("Sample Rate")[1]);
            if (file.SampleRate <= 0 || duration <= 0) throw new InvalidDataException("Invalid sample rate or duration");
            string[] rms = Read("RMS");
            if (rms.Length < channels + 1) throw new InvalidDataException("Missing RMS channels");
            file.Rms = new double[channels];
            for (int c = 0; c < channels; c++) file.Rms[c] = Number(rms[c + 1]);
            file.Count = int.Parse(Read("Data Length")[1].Trim(), CultureInfo.InvariantCulture);
            if (file.Count <= 0 || file.Count > 10000000 || Math.Abs(file.Count / file.SampleRate - duration) > 0.000001)
                throw new InvalidDataException("Invalid data length / duration");
            file.Samples = new double[checked(file.Count * channels)];
            for (int i = 0; i < file.Count; i++)
            {
                string line = reader.ReadLine() ?? throw new InvalidDataException("Truncated samples at " + i);
                string[] cells = line.Split(',');
                if (cells.Length < channels + 1) throw new InvalidDataException("Missing channels at " + i);
                for (int c = channels + 1; c < cells.Length; c++)
                    if (!string.IsNullOrWhiteSpace(cells[c])) throw new InvalidDataException("Unexpected channel at " + i);
                if (Math.Abs(Number(cells[0]) - i / file.SampleRate) > 0.000001)
                    throw new InvalidDataException("Invalid sample timestamp at " + i);
                for (int c = 0; c < channels; c++) file.Samples[i * channels + c] = Number(cells[c + 1]);
            }
            if (reader.ReadLine() != null) throw new InvalidDataException("Extra sample rows");
            return file;
        }
    }
}
