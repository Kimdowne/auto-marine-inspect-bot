using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShipRobot.EquipmentMonitoring;

internal static class Program
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args.Length > 0 ? args[0] : "data");
            var formats = new Dictionary<string, int>();
            int count = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*.csv", SearchOption.AllDirectories))
            {
                string[] parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
                Check(parts.Length == 5 && (parts[0] == "current" || parts[0] == "vibration"), "Unexpected path: " + path);
                CsvSignal signal;
                try { signal = CsvSignal.Load(path, parts[0] == "current" ? 3 : 1, parts[2], parts[3]); }
                catch (Exception ex) { throw new Exception(path + ": " + ex.Message, ex); }
                string key = $"{parts[0]}: {signal.Count} samples, {signal.SampleRate}Hz";
                formats[key] = formats.TryGetValue(key, out int n) ? n + 1 : 1;
                count++;
            }
            Check(count > 0, "No dataset found");
            Console.WriteLine("Validated CSV files: " + count);
            foreach (var item in formats) Console.WriteLine(item.Key + " -> " + item.Value);

            var a = new CsvReplaySession("A");
            var b = new CsvReplaySession("B");
            a.Start(root, "2.2kW", "L-DSF-01", "정상", true);
            b.Start(root, "2.2kW", "L-SF-04", "베어링불량", true);
            Check(a.Latest.State == DataSourceState.Playing && b.Latest.State == DataSourceState.Playing, "Default A/B profiles");
            a.Pause(); b.Tick(0.2);
            Check(a.Latest.ReplaySeconds == 0 && b.Latest.ReplaySeconds == 0.2, "Equipment independence");
            a.Start(root, "2.2kW", "L-DSF-01", "축정렬불량", true);
            Check(a.Latest.State == DataSourceState.Playing && a.Latest.ReplaySeconds == 0 && a.Latest.SimulationLabel == "축정렬불량", "State switch reset");
            Console.WriteLine("PASS: A/B profiles, independent equipment clocks, normal/fault switch.");

            // Two real recordings in an isolated temporary fixture, no source dataset mutations.
            string temp = Path.Combine(Path.GetTempPath(), "equipment-replay-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (string kind in new[] { "current", "vibration" })
                {
                    string dir = Path.Combine(temp, kind, "2.2kW", "L-DSF-01", "정상");
                    Directory.CreateDirectory(dir);
                    string original = Directory.GetFiles(Path.Combine(root, kind, "2.2kW", "L-DSF-01", "정상"), "*.csv").OrderBy(x => x, StringComparer.Ordinal).First();
                    File.Copy(original, Path.Combine(dir, "01.csv"));
                }
                var session = new CsvReplaySession("A");
                session.Start(temp, "2.2kW", "L-DSF-01", "정상", true);
                Check(session.Latest.State == DataSourceState.Playing, session.Latest.Error);
                var before = session.Latest;
                Check(before.Current.Values.Count == 3 && before.Vibration.Values.Count == 1, "Channels");
                session.Tick(0.5);
                Check(session.Latest.Current.PositionSeconds == 0.5, "Sample clock");
                Check(before.Current.PositionSeconds == 0, "Snapshot must remain immutable");
                session.Pause(); session.Tick(2);
                Check(session.Latest.ReplaySeconds == 0.5 && session.Latest.State == DataSourceState.Paused, "Pause");
                session.Resume(); session.Tick(0.75);
                Check(session.Latest.Current.PositionSeconds == 0.25 && session.Latest.Vibration.PositionSeconds == 1.25, "Independent wrap");
                Check(session.Latest.Diagnosis == "NotEvaluated", "Label leakage");
                session.Start(temp, "2.2kW", "L-DSF-01", "정상", false);
                session.Tick(1.5);
                Check(session.Latest.Current.Completed && !session.Latest.Vibration.Completed, "Independent completion");
                session.Tick(2);
                Check(session.Latest.State == DataSourceState.Completed, "End without loop");
                session.Start(temp, "2.2kW", "L-DSF-01", "missing", true);
                Check(session.Latest.State == DataSourceState.Error && session.Latest.Current == null, "No stale data on error");
                session.Start(temp, "../outside", "L-DSF-01", "정상", true);
                Check(session.Latest.State == DataSourceState.Error, "Reject path traversal");
                session.Start(temp, "2.2kW", "L-DSF-01", "정상", true);
                session.Stop();
                Check(session.Latest.State == DataSourceState.Stopped && session.Latest.Current == null, "Stop");
                string fixture = Path.Combine(temp, "current", "2.2kW", "L-DSF-01", "정상", "01.csv");
                string text = File.ReadAllText(fixture);
                File.WriteAllText(fixture, text.Replace("Data Length,2000", "Data Length,2001"));
                session.Start(temp, "2.2kW", "L-DSF-01", "정상", true);
                Check(session.Latest.State == DataSourceState.Error, "Reject invalid length");
                File.WriteAllText(fixture, text);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture), "02.csv"), "invalid");
                session.Start(temp, "2.2kW", "L-DSF-01", "정상", true);
                session.Tick(1);
                Check(session.Latest.State == DataSourceState.Error && session.Latest.Vibration == null, "Bad next file stops both streams");
                Console.WriteLine("PASS: sample selection, immutable snapshot, pause/resume, independent wrap/completion, no label-derived diagnosis, missing data, path validation, stop, malformed data, next-file failure.");
            }
            finally
            {
                string resolved = Path.GetFullPath(temp);
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("equipment-replay-test-", StringComparison.Ordinal))
                    Directory.Delete(resolved, true);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
