using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
int port = args.Length == 0 ? 8765 : int.Parse(args[0]);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
using var client = new MonitorClient(port);
Task connection = client.Run(shutdown.Token);
Console.WriteLine("Unity 설비 모니터 · 명령: A pause/resume/restart/stop/normal/fault (B도 동일), quit");
try
{
    while (!shutdown.IsCancellationRequested)
    {
        string? line = await Console.In.ReadLineAsync(shutdown.Token);
        if (line == null || line.Trim() == "quit") break;
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2) { Console.WriteLine("예: A pause"); continue; }
        await client.Send(words[0], words[1], shutdown.Token);
    }
}
catch (OperationCanceledException) { }
finally { shutdown.Cancel(); await connection; }

sealed class MonitorClient(int port) : IDisposable
{
    private readonly object gate = new();
    private TcpClient? socket;
    private StreamWriter? writer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pending = new();
    public async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, token);
                tcp.NoDelay = true; tcp.SendTimeout = 2000;
                using var reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false, true));
                using var output = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                lock (gate) { socket = tcp; writer = output; }
                Console.WriteLine($"연결됨: 127.0.0.1:{port}");
                DateTime nextDisplay = DateTime.MinValue;
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
                    if (line == null) throw new IOException("Server closed connection");
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.GetProperty("version").GetInt32() != 1) throw new IOException("Unsupported protocol version");
                    string? type = root.GetProperty("type").GetString();
                    if (type == "commandResult")
                    {
                        string? id = root.GetProperty("commandId").GetString();
                        if (id != null && pending.TryRemove(id, out var completion)) completion.TrySetResult(line);
                        else Console.WriteLine("명령 응답: " + line);
                    }
                    else if (type == "telemetry" && DateTime.UtcNow >= nextDisplay)
                    {
                        nextDisplay = DateTime.UtcNow.AddSeconds(1);
                        foreach (JsonElement equipment in root.GetProperty("equipment").EnumerateArray())
                        {
                            string Signal(string name)
                            {
                                JsonElement signal = equipment.GetProperty(name);
                                return signal.ValueKind == JsonValueKind.Null ? "없음" : signal.GetProperty("values").GetRawText();
                            }
                            Console.WriteLine($"[{equipment.GetProperty("equipmentId")}] {equipment.GetProperty("state")} / 설정={equipment.GetProperty("simulationLabel")} / 진단={equipment.GetProperty("diagnosis")} / 전류={Signal("current")} / 진동={Signal("vibration")}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or TimeoutException or InvalidOperationException or KeyNotFoundException)
            { Console.WriteLine("연결 끊김/데이터 만료: " + ex.Message + " · 2초 후 재연결"); }
            finally
            {
                lock (gate) { writer = null; socket = null; }
                foreach (var item in pending) if (pending.TryRemove(item.Key, out var completion)) completion.TrySetResult("연결 끊김: 명령 처리 여부 미확인. 자동 재전송하지 않습니다.");
            }
            try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Send(string equipment, string action, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string command = JsonSerializer.Serialize(new { version = 1, type = "command", commandId = id, equipmentId = equipment, action });
        lock (gate)
        {
            if (writer == null) { Console.WriteLine("연결되지 않았습니다. 명령은 저장하지 않습니다."); return; }
            pending[id] = completion;
            try { writer.WriteLine(command); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            { pending.TryRemove(id, out _); Console.WriteLine("전송 실패, 처리 여부 미확인: " + ex.Message); socket?.Close(); return; }
        }
        try { Console.WriteLine(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token)); }
        catch (TimeoutException) { Console.WriteLine("응답 시간 초과: 처리 여부 미확인. 자동 재전송하지 않습니다."); }
        finally { pending.TryRemove(id, out _); }
    }
    public void Dispose() { lock (gate) socket?.Dispose(); }
}
