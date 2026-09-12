using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MarineMonitor.Core;

// No UI or Unity dependency. A WPF timer consumes the latest snapshot without a growing UI queue.
public sealed class MonitorConnection
{
    private sealed class Session(TcpClient socket, StreamWriter writer)
    {
        public TcpClient Socket { get; } = socket;
        public StreamWriter Writer { get; } = writer;
        public ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> Pending { get; } = new();
    }
    private readonly object gate = new();
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly ConcurrentQueue<ClientEvent> events = new();
    private Session? active;
    private CancellationTokenSource? cancellation;
    private Task? run;
    private ReceivedTelemetry? latest;
    private long generation;
    private string status = "연결 안 됨";
    public string Status => Volatile.Read(ref status);
    public ReceivedTelemetry? Latest => Volatile.Read(ref latest);
    public bool IsConnected { get { lock (gate) return active != null; } }
    public bool IsRunning => run is { IsCompleted: false };
    public bool TryReadEvent(out ClientEvent? item) => events.TryDequeue(out item);
    private void Log(string message)
    {
        events.Enqueue(new ClientEvent(DateTime.Now, message));
        while (events.Count > 200) events.TryDequeue(out _);
    }
    public void Connect(int port)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (IsRunning) return;
        cancellation?.Dispose(); cancellation = new CancellationTokenSource();
        Volatile.Write(ref latest, null);
        run = Task.Run(() => Run(port, cancellation.Token));
    }
    public async Task DisconnectAsync()
    {
        cancellation?.Cancel();
        lock (gate) active?.Socket.Close();
        if (run != null) await run.ConfigureAwait(false);
        Volatile.Write(ref latest, null);
        Volatile.Write(ref status, "연결 안 됨");
    }
    private async Task Run(int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Session? session = null;
            try
            {
                Volatile.Write(ref status, "연결 중");
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false);
                tcp.NoDelay = true;
                using var reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false, true));
                using var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                session = new Session(tcp, writer);
                lock (gate) active = session;
                long id = Interlocked.Increment(ref generation);
                Volatile.Write(ref status, "연결됨"); Log($"Unity 연결됨 · 127.0.0.1:{port}");
                while (!token.IsCancellationRequested)
                {
                    string json = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false)
                        ?? throw new IOException("Unity가 연결을 종료했습니다");
                    if (json.Length > 262144) throw new IOException("메시지 크기 제한 초과");
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                    var root = document.RootElement;
                    if (root.GetProperty("version").GetInt32() != 1) throw new JsonException("지원하지 않는 프로토콜");
                    switch (root.GetProperty("type").GetString())
                    {
                        case "telemetry":
                            Volatile.Write(ref latest, new ReceivedTelemetry(Telemetry.Parse(json), DateTime.UtcNow, id)); break;
                        case "commandResult":
                            string? commandId = root.GetProperty("commandId").GetString();
                            var result = new CommandResult(root.GetProperty("ok").GetBoolean(), root.GetProperty("code").GetString() ?? "",
                                root.GetProperty("message").GetString() ?? "", commandId ?? "");
                            if (commandId != null && session.Pending.TryRemove(commandId, out var response)) response.TrySetResult(result);
                            else Log("지연/미등록 명령 응답: " + result.Message);
                            break;
                        default: throw new JsonException("지원하지 않는 메시지 종류");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or JsonException or InvalidOperationException or KeyNotFoundException or ObjectDisposedException or DecoderFallbackException or FormatException)
            { if (!token.IsCancellationRequested) Log("연결 끊김 · " + ex.Message); }
            finally
            {
                lock (gate) if (ReferenceEquals(active, session)) active = null;
                Volatile.Write(ref latest, null);
                if (session != null) foreach (var item in session.Pending)
                    if (session.Pending.TryRemove(item.Key, out var response)) response.TrySetResult(new(false, "unknown", "연결 끊김: 처리 여부 미확인 · 자동 재전송 없음", item.Key));
            }
            if (token.IsCancellationRequested) break;
            Volatile.Write(ref status, "재연결 대기 (2초)");
            try { await Task.Delay(2000, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        Volatile.Write(ref status, "연결 안 됨"); Log("연결 종료");
    }
    public async Task<CommandResult> SendAsync(string equipmentId, string action)
    {
        string id = Guid.NewGuid().ToString("N");
        Session? session; lock (gate) session = active;
        if (session == null) return new(false, "disconnected", "연결되지 않았습니다. 명령은 저장하지 않습니다.", id);
        var response = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool writing = false;
        try
        {
            await writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                lock (gate)
                {
                    if (!ReferenceEquals(active, session)) return new(false, "disconnected", "연결이 변경되었습니다", id);
                    session.Pending[id] = response;
                }
                string json = JsonSerializer.Serialize(new { version = 1, type = "command", commandId = id, equipmentId, action });
                writing = true;
                await session.Writer.WriteLineAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            finally { writeGate.Release(); }
            return await response.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (writing && !response.Task.IsCompleted) session.Socket.Close();
            return new(false, "unknown", "전송/응답 실패: 처리 여부 미확인 · 자동 재전송 없음", id);
        }
        finally { session.Pending.TryRemove(id, out _); }
    }
}
