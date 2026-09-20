using System.Diagnostics;
using MarineMonitor.Core;
using ShipRobot.EquipmentMonitoring;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task WaitFor(Func<bool> condition)
{
    var until = DateTime.UtcNow.AddSeconds(15);
    while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Test condition timed out"); await Task.Delay(30); }
}
string root = Path.GetFullPath("data");
var a = new CsvReplaySession("A"); var b = new CsvReplaySession("B");
a.Start(root, "2.2kW", "L-DSF-01", "정상", true);
b.Start(root, "2.2kW", "L-SF-04", "베어링불량", true);
Check(a.Latest.State == DataSourceState.Playing && b.Latest.State == DataSourceState.Playing, "CSV fixture");
var parsed = Telemetry.Parse(EquipmentWireProtocol.Telemetry(1, new[] { a.Latest, b.Latest }));
Check(parsed.Equipment[1].SimulationLabel == "베어링불량" && parsed.Equipment[0].Current!.Values.Length == 3, "Unity protocol compatibility");
bool malformedRejected = false;
try { Telemetry.Parse("{\"version\":2,\"type\":\"telemetry\"}"); } catch (System.Text.Json.JsonException) { malformedRejected = true; }
Check(malformedRejected, "Protocol mismatch rejected");
var server = new EquipmentTcpServer(); server.Start(0); int port = server.Port;
EquipmentTcpServer? transport = server;
using var stop = new CancellationTokenSource();
var router = new EquipmentCommandRouter();
Task host = Task.Run(async () =>
{
    long sequence = 0;
    while (!stop.IsCancellationRequested)
    {
        var current = Volatile.Read(ref transport);
        current?.Pump((id, json) => router.Handle(id, json, (equipment, action) =>
        {
            var target = equipment == "A" ? a : equipment == "B" ? b : null;
            if (target == null) return "Unknown equipment";
            switch (action)
            {
                case "pause": target.Pause(); break;
                case "resume": target.Resume(); break;
                case "stop": target.Stop(); break;
                default: return "Unsupported action";
            }
            return null;
        }));
        a.Tick(.05); b.Tick(.05);
        current?.Publish(EquipmentWireProtocol.Telemetry(++sequence, new[] { a.Latest, b.Latest }));
        await Task.Delay(50);
    }
});
var client = new MonitorConnection();
try
{
    Check(!(await client.SendAsync("A", "pause")).Ok, "Disconnected command rejected");
    client.Connect(port); await WaitFor(() => client.Latest != null);
    Check((await client.SendAsync("A", "pause")).Ok, "Pause ACK");
    await WaitFor(() => client.Latest?.Data.Equipment[0].State == "Paused");
    Check(client.Latest!.Data.Equipment[1].State == "Playing", "B independent");
    Check(!(await client.SendAsync("A", "unsupported")).Ok, "Server failure response");
    Check((await client.SendAsync("A", "resume")).Ok, "Resume ACK");
    long generation = client.Latest.ConnectionId;
    Volatile.Write(ref transport, null); server.Dispose();
    await WaitFor(() => !client.IsConnected && client.Latest == null);
    server = new EquipmentTcpServer(); server.Start(port); Volatile.Write(ref transport, server);
    await WaitFor(() => client.Latest != null && client.Latest.ConnectionId != generation);
    await client.DisconnectAsync();
    Check(!client.IsRunning && client.Latest == null, "Explicit disconnect cleans snapshot");
    Console.WriteLine("PASS: Unity telemetry parser; disconnected commands; A/B receipt; pause/resume; rejection; connection loss clears data; automatic reconnect; explicit disconnect.");
    if (args.Length > 0)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--smoke"); start.ArgumentList.Add(port.ToString()); start.ArgumentList.Add(Path.GetFullPath("gui/artifacts/wpf-smoke"));
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            Check(process.ExitCode == 0, "WPF smoke failed; see gui/artifacts/wpf-smoke.txt");
            Console.WriteLine(await File.ReadAllTextAsync("gui/artifacts/wpf-smoke.txt"));
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
finally { await client.DisconnectAsync(); stop.Cancel(); await host; server.Dispose(); }
