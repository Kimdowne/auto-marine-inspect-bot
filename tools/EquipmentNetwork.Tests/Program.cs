using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using ShipRobot.EquipmentMonitoring;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<JObject> ReadType(StreamReader reader, string type)
{
    using var timeout = new CancellationTokenSource(5000);
    while (true)
    {
        string line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Unexpected disconnect");
        var json = JObject.Parse(line);
        if ((string)json["type"] == type) return json;
    }
}
static string Command(string id, string action) => new JObject
{
    ["version"] = 1, ["type"] = "command", ["commandId"] = id,
    ["equipmentId"] = "A", ["action"] = action
}.ToString(Newtonsoft.Json.Formatting.None);

var router = new EquipmentCommandRouter();
int executions = 0;
string Run(string id, string action) { executions++; return action == "bad" ? "Unsupported action" : null; }
string first = router.Handle(1, Command("same", "pause"), Run);
Check(router.Handle(1, Command("same", "pause"), Run) == first && executions == 1, "Duplicate executes once");
Check((string)JObject.Parse(router.Handle(1, Command("same", "stop"), Run))["code"] == "id_conflict", "Conflict rejected");
Check(!(bool)JObject.Parse(router.Handle(1, "{bad", Run))["ok"], "Malformed JSON");
Check(!(bool)JObject.Parse(router.Handle(1, Command("x", "bad"), Run))["ok"], "Unknown action");
router.Handle(2, Command("same", "pause"), Run);
Check(executions == 3, "Reconnect resets response cache");

var a = new CsvReplaySession("A");
var b = new CsvReplaySession("B");
string root = Path.GetFullPath("data");
a.Start(root, "2.2kW", "L-DSF-01", "정상", true);
b.Start(root, "2.2kW", "L-SF-04", "베어링불량", true);
Check(a.Latest.State == DataSourceState.Playing && b.Latest.State == DataSourceState.Playing, "Dataset loaded");
using var server = new EquipmentTcpServer();
server.Start(0);
using (var collision = new EquipmentTcpServer())
{
    bool rejected = false;
    try { collision.Start(server.Port); } catch (SocketException) { rejected = true; }
    Check(rejected, "Occupied port rejected");
}
using var shutdown = new CancellationTokenSource();
var networkRouter = new EquipmentCommandRouter();
Task host = Task.Run(async () =>
{
    long sequence = 0;
    while (!shutdown.IsCancellationRequested)
    {
        server.Pump((id, json) => networkRouter.Handle(id, json, (equipment, action) =>
        {
            if (equipment != "A") return "Unknown equipment";
            switch (action) { case "pause": a.Pause(); break; case "resume": a.Resume(); break; default: return "Unsupported action"; }
            return null;
        }));
        a.Tick(0.05); b.Tick(0.05);
        server.Publish(EquipmentWireProtocol.Telemetry(++sequence, new[] { a.Latest, b.Latest }));
        await Task.Delay(50);
    }
});
try
{
    using (var tcp = new TcpClient())
    {
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
        using var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        JObject telemetry = await ReadType(reader, "telemetry");
        Check(telemetry["equipment"].Count() == 2, "A/B telemetry");
        Check((string)telemetry["equipment"][1]["simulationLabel"] == "베어링불량", "UTF-8 round trip");
        Check((string)telemetry["equipment"][0]["diagnosis"] == "NotEvaluated", "Diagnosis separate");
        Check(telemetry["equipment"][0]["current"]["values"].Count() == 3, "Three current channels");
        await writer.WriteLineAsync(Command("pause1", "pause"));
        Check((bool)(await ReadType(reader, "commandResult"))["ok"], "Pause acknowledgement");
        telemetry = await ReadType(reader, "telemetry");
        Check((string)telemetry["equipment"][0]["state"] == "Paused" && (string)telemetry["equipment"][1]["state"] == "Playing", "Independent paused telemetry");
        await writer.WriteLineAsync("{broken");
        Check(!(bool)(await ReadType(reader, "commandResult"))["ok"], "Invalid JSON response keeps connection alive");
        await writer.WriteLineAsync(Command("resume1", "resume"));
        Check((bool)(await ReadType(reader, "commandResult"))["ok"], "Resume acknowledgement");
        // An oversized command closes this connection without executing it.
        await writer.WriteLineAsync(new string('x', 5000));
        using var timeout = new CancellationTokenSource(5000);
        while (await reader.ReadLineAsync(timeout.Token) != null) { }
    }
    using (var reconnect = new TcpClient())
    {
        await reconnect.ConnectAsync("127.0.0.1", server.Port);
        using var reader = new StreamReader(reconnect.GetStream(), Encoding.UTF8);
        Check((await ReadType(reader, "telemetry"))["equipment"].Count() == 2, "Reconnect telemetry");
    }
    if (args.Length > 0)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.GetFullPath(args[0])); start.ArgumentList.Add(server.Port.ToString());
        using var process = Process.Start(start);
        try
        {
            async Task WaitFor(string text)
            {
                using var timeout = new CancellationTokenSource(10000);
                while (true)
                {
                    string line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    if (line == null) throw new Exception("Client exited: " + await process.StandardError.ReadToEndAsync());
                    if (line.Contains(text)) return;
                }
            }
            await WaitFor("[A]");
            await process.StandardInput.WriteLineAsync("A pause");
            await process.StandardInput.FlushAsync();
            await WaitFor("\"ok\":true");
            await process.StandardInput.WriteLineAsync("quit");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.ExitCode == 0, "Console clean exit");
            Console.WriteLine("PASS: separate console process receives telemetry, sends pause, receives ACK, exits cleanly.");
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    Console.WriteLine("PASS: Unity-reference compile; protocol validation; duplicate/conflicting IDs; real A/B TCP telemetry; Korean labels; pause/resume ACK and state; malformed JSON; oversized command disconnect; reconnect; occupied port.");
}
finally { shutdown.Cancel(); await host; }
