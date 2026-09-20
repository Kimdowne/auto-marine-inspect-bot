using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Debug = UnityEngine.Debug;

namespace JetBot.Networking
{
    [DisallowMultipleComponent]
    public sealed class JetBotTcpController : MonoBehaviour
    {
        [Header("Connection (changes apply after disable / enable)")]
        [SerializeField] private string jetsonIP = "192.168.1.111";
        [SerializeField] private int port = 9000;
        [SerializeField] private float sendRate = 20f;
        [SerializeField] private bool autoReconnect = true;
        [SerializeField] private float reconnectInterval = 2f;
        [SerializeField] private float connectTimeout = 3f;

        [Header("Control")]
        [Tooltip("ESC stops this component only; it does not quit Unity.")]
        [SerializeField] private bool escapeStopsController = true;
        [Tooltip("Send neutral commands if Update has not refreshed input in this time.")]
        [SerializeField] private float inputStaleTimeout = 0.2f;
        [SerializeField] private bool logEveryAck;

        [Header("Diagnostics (runtime display)")]
        [SerializeField] private string connectionStatus = "Stopped";
        [SerializeField, TextArea(2, 5)] private string lastAck = "None";
        public bool IsConnected => worker != null && worker.Connected;
        public string LastAck => lastAck;

        private Worker worker;
        private bool wanted;
        private bool focused;
        private bool paused;
        private float nextAckLog;

        private void OnEnable()
        {
            wanted = true;
            focused = Application.isFocused;
            paused = false;
            StartIfReady();
        }

        private void StartIfReady()
        {
            if (!wanted || (worker != null && !worker.Completion.IsCompleted)) return;
            IPAddress address;
            if (!IPAddress.TryParse(jetsonIP, out address) || port < 1 || port > 65535)
            {
                wanted = false;
                connectionStatus = "Invalid IP / port";
                Debug.LogError("[JetBot TCP] Enter a numeric Jetson IP and a valid port.", this);
                return;
            }
#if !ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            wanted = false;
            Debug.LogError("[JetBot] No supported input backend is enabled.", this);
            return;
#endif
            double hz = Positive(sendRate, 20);
            // Avoid accidental busy loops / huge allocations from invalid settings.
            hz = Math.Min(hz, 200);
            worker = new Worker(address, port, hz, autoReconnect,
                Math.Min(3600, Math.Max(0.1, Positive(reconnectInterval, 2))),
                Math.Min(60, Positive(connectTimeout, 3)),
                Math.Min(10, Positive(inputStaleTimeout, 0.2)));
            lastAck = "None";
            nextAckLog = 0;
            worker.Start();
        }

        private static double Positive(float value, double fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value <= 0
                ? fallback : value;
        }

        private void Update()
        {
            // Re-enabling waits for the previous worker to finish, without blocking.
            if (wanted && worker != null && worker.Stopping && worker.Completion.IsCompleted)
                StartIfReady();
            if (worker == null) return;

            bool w = false, s = false, a = false, d = false;
            bool one = false, two = false, three = false, escape = false;
            if (focused && !paused)
            {
#if ENABLE_INPUT_SYSTEM
                var keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    w = keyboard.wKey.isPressed; s = keyboard.sKey.isPressed;
                    a = keyboard.aKey.isPressed; d = keyboard.dKey.isPressed;
                    one = keyboard.digit1Key.isPressed;
                    two = keyboard.digit2Key.isPressed;
                    three = keyboard.digit3Key.isPressed;
                    escape = keyboard.escapeKey.wasPressedThisFrame;
                }
#elif ENABLE_LEGACY_INPUT_MANAGER
                w = Input.GetKey(KeyCode.W); s = Input.GetKey(KeyCode.S);
                a = Input.GetKey(KeyCode.A); d = Input.GetKey(KeyCode.D);
                one = Input.GetKey(KeyCode.Alpha1);
                two = Input.GetKey(KeyCode.Alpha2);
                three = Input.GetKey(KeyCode.Alpha3);
                escape = Input.GetKeyDown(KeyCode.Escape);
#endif
            }
            worker.Publish(new Control(B(w) - B(s), B(a) - B(d), B(one), B(two), B(three)));
            if (escape && escapeStopsController)
            {
                Debug.Log("[JetBot] ESC: controller stopped. Re-enable to reconnect.", this);
                enabled = false;
                return;
            }

            string message;
            for (int i = 0; i < 32 && worker.Logs.TryTake(out message); i++)
                Debug.Log(message, this);
            connectionStatus = worker.Status;

            Received received;
            for (int i = 0; i < 64 && worker.Acks.TryTake(out received); i++)
                DisplayAck(received);
        }

        private static int B(bool value) => value ? 1 : 0;

        [Serializable]
        private sealed class Ack
        {
            public int ack = int.MinValue;
            public int[] drive;
            public int[] servo;
            public int left = int.MinValue;
            public int right = int.MinValue;
            public int nucleo = int.MinValue;
        }

        private void DisplayAck(Received received)
        {
            try
            {
                var ack = new Ack();
                // Missing fields retain sentinel values instead of silently becoming zero.
                JsonUtility.FromJsonOverwrite(received.Line, ack);
                if (ack.ack <= 0 || ack.drive == null || ack.drive.Length != 2 ||
                    ack.servo == null || ack.servo.Length != 3 ||
                    ack.left == int.MinValue || ack.right == int.MinValue ||
                    ack.nucleo == int.MinValue)
                    throw new FormatException("Missing / invalid ACK fields");
                foreach (int v in ack.drive)
                    if (v < -1 || v > 1) throw new FormatException("Invalid drive");
                foreach (int v in ack.servo)
                    if (v < 0 || v > 1) throw new FormatException("Invalid servo");

                Sent sent;
                string check = "unknown/late";
                if (worker.TakeSent(ack.ack, out sent))
                {
                    Control c = sent.Control;
                    bool pass = ack.drive[0] == c.Linear && ack.drive[1] == c.Turn &&
                        ack.servo[0] == c.One && ack.servo[1] == c.Two && ack.servo[2] == c.Three;
                    double rtt = (received.Tick - sent.Tick) * 1000.0 / Stopwatch.Frequency;
                    check = "RTT=" + rtt.ToString("F2", CultureInfo.InvariantCulture) +
                        "ms VERIFY=" + (pass ? "PASS" : "FAIL");
                }
                lastAck = "seq=" + ack.ack + " drive=[" + string.Join(",", ack.drive) +
                    "] servo=[" + string.Join(",", ack.servo) + "] left=" + ack.left +
                    " right=" + ack.right + " nucleo=" + ack.nucleo + " " + check;
                if (logEveryAck || Time.unscaledTime >= nextAckLog)
                {
                    Debug.Log("[JetBot ACK] " + lastAck, this);
                    nextAckLog = Time.unscaledTime + 1;
                }
            }
            catch (Exception ex)
            {
                lastAck = "Invalid ACK: " + ex.Message;
                if (Time.unscaledTime >= nextAckLog)
                {
                    Debug.LogWarning("[JetBot ACK] " + lastAck, this);
                    nextAckLog = Time.unscaledTime + 1;
                }
            }
        }

        private void OnApplicationFocus(bool value)
        {
            focused = value;
            if (!value) worker?.Publish(new Control());
        }

        private void OnApplicationPause(bool value)
        {
            paused = value;
            if (value) worker?.Publish(new Control());
        }

        private void StopController()
        {
            wanted = false;
            connectionStatus = "Stopping";
            worker?.RequestStop();
        }

        private void OnDisable() => StopController();
        private void OnDestroy() => StopController();
        private void OnApplicationQuit() => StopController();

        // All types below use only ordinary C# data and sockets. No Unity API calls.
        private sealed class Control
        {
            public readonly int Linear, Turn, One, Two, Three;
            public readonly long Tick = Stopwatch.GetTimestamp();
            public Control(int linear = 0, int turn = 0, int one = 0, int two = 0, int three = 0)
            { Linear = linear; Turn = turn; One = one; Two = two; Three = three; }
        }

        private sealed class Received
        {
            public readonly string Line;
            public readonly long Tick = Stopwatch.GetTimestamp();
            public Received(string line) { Line = line; }
        }

        private sealed class Sent
        {
            public readonly Control Control;
            public readonly long Tick = Stopwatch.GetTimestamp();
            public Sent(Control control) { Control = control; }
        }

        private sealed class BoundedQueue<T>
        {
            private readonly Queue<T> queue = new Queue<T>();
            private readonly int capacity;
            public BoundedQueue(int capacity) { this.capacity = capacity; }
            public void Add(T item)
            {
                lock (queue)
                {
                    if (queue.Count == capacity) queue.Dequeue();
                    queue.Enqueue(item);
                }
            }
            public bool TryTake(out T item)
            {
                lock (queue)
                {
                    if (queue.Count == 0) { item = default(T); return false; }
                    item = queue.Dequeue(); return true;
                }
            }
        }

        private sealed class Worker
        {
            private readonly IPAddress address;
            private readonly int port;
            private readonly double period, reconnectSeconds, timeoutSeconds, staleSeconds;
            private readonly bool reconnect;
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            private readonly object socketGate = new object();
            private readonly Dictionary<int, Sent> history = new Dictionary<int, Sent>();
            private readonly Queue<int> order = new Queue<int>();
            private TcpClient client;
            private Control current = new Control();
            private int stopRequested, sequence;
            public readonly BoundedQueue<string> Logs = new BoundedQueue<string>(64);
            public readonly BoundedQueue<Received> Acks = new BoundedQueue<Received>(256);
            public volatile bool Connected;
            public volatile string Status = "Starting";
            public Task Completion { get; private set; } = Task.CompletedTask;
            public bool Stopping => Volatile.Read(ref stopRequested) != 0;

            public Worker(IPAddress address, int port, double hz, bool reconnect,
                double reconnectSeconds, double timeoutSeconds, double staleSeconds)
            {
                this.address = address; this.port = port; period = 1.0 / hz;
                this.reconnect = reconnect; this.reconnectSeconds = reconnectSeconds;
                this.timeoutSeconds = timeoutSeconds; this.staleSeconds = staleSeconds;
            }

            public void Start() { Completion = Task.Run(RunAsync); }
            public void Publish(Control value) { Volatile.Write(ref current, value); }

            public bool TakeSent(int seq, out Sent sent)
            {
                lock (history)
                {
                    if (!history.TryGetValue(seq, out sent)) return false;
                    history.Remove(seq); return true;
                }
            }

            private void SetStatus(string value)
            {
                Status = value;
                Logs.Add("[JetBot TCP] " + value);
            }

            public void RequestStop()
            {
                if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
                Publish(new Control());
                stop.Cancel();
                // Main thread never waits. Abort blocked I/O after a short STOP grace period.
                _ = AbortAfterGraceAsync();
            }

            private async Task AbortAfterGraceAsync()
            {
                await Task.Delay(150).ConfigureAwait(false);
                if (!Completion.IsCompleted) CloseSocket();
            }

            private void CloseSocket()
            {
                lock (socketGate)
                {
                    try { client?.Close(); } catch (Exception) { }
                }
            }

            private async Task RunAsync()
            {
                try
                {
                    do
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
                        lock (socketGate) client = tcp;
                        using (var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                        {
                            Task sending = Task.CompletedTask, receiving = Task.CompletedTask;
                            try
                            {
                                SetStatus("Connecting " + address + ":" + port);
                                await WithDeadline(tcp.ConnectAsync(address, port),
                                    timeoutSeconds, stop.Token).ConfigureAwait(false);
                                stop.Token.ThrowIfCancellationRequested();
                                Connected = true;
                                SetStatus("Connected " + address + ":" + port);
                                NetworkStream stream = tcp.GetStream();
                                sending = SendLoopAsync(stream, session.Token);
                                receiving = ReceiveLoopAsync(stream, session.Token);
                                Task first = await Task.WhenAny(sending, receiving).ConfigureAwait(false);
                                await first.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                            catch (Exception ex)
                            {
                                if (!stop.IsCancellationRequested)
                                    SetStatus("Disconnected / connection failed: " + ex.Message);
                            }
                            finally
                            {
                                session.Cancel();
                                // Closing the socket unblocks reads that ignore cancellation.
                                tcp.Close();
                                await IgnoreFailure(sending).ConfigureAwait(false);
                                await IgnoreFailure(receiving).ConfigureAwait(false);
                                Connected = false;
                                lock (socketGate) { if (client == tcp) client = null; }
                            }
                        }
                        if (!reconnect || stop.IsCancellationRequested) break;
                        SetStatus("Reconnect in " + reconnectSeconds + "s");
                        await Task.Delay(TimeSpan.FromSeconds(reconnectSeconds), stop.Token)
                            .ConfigureAwait(false);
                    } while (!stop.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) { SetStatus("Worker ended: " + ex.Message); }
                finally { Connected = false; CloseSocket(); SetStatus("Stopped"); }
            }

            private async Task SendLoopAsync(NetworkStream stream, CancellationToken token)
            {
                var clock = Stopwatch.StartNew();
                double next = 0;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        double wait = next - clock.Elapsed.TotalSeconds;
                        if (wait > 0)
                            await Task.Delay(TimeSpan.FromSeconds(wait), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        Control value = Volatile.Read(ref current);
                        if ((Stopwatch.GetTimestamp() - value.Tick) / (double)Stopwatch.Frequency > staleSeconds)
                            value = new Control();
                        await WriteCommandAsync(stream, value, token, 0.5).ConfigureAwait(false);
                        next += period;
                        // Do not burst stale packets after a scheduling / network stall.
                        if (next < clock.Elapsed.TotalSeconds)
                            next = clock.Elapsed.TotalSeconds + period;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Same writer, so STOP cannot interleave with a normal JSON packet.
                    try
                    {
                        await WriteCommandAsync(stream, new Control(), CancellationToken.None, 0.1)
                        .ConfigureAwait(false);
                    }
                    catch (Exception) { }
                }
            }

            private async Task WriteCommandAsync(NetworkStream stream, Control value,
                CancellationToken token, double timeout)
            {
                if (sequence == int.MaxValue) throw new InvalidOperationException("seq exhausted; restart component");
                int seq = ++sequence;
                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\"seq\":{0},\"ts\":{1},\"drive\":[{2},{3}],\"servo\":[{4},{5},{6}]}}\n",
                    seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    value.Linear, value.Turn, value.One, value.Two, value.Three);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                lock (history)
                {
                    history[seq] = new Sent(value); order.Enqueue(seq);
                    while (order.Count > 200) history.Remove(order.Dequeue());
                }
                await WithDeadline(stream.WriteAsync(bytes, 0, bytes.Length, token), timeout, token)
                    .ConfigureAwait(false);
            }

            private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
            {
                byte[] buffer = new byte[4096];
                var line = new List<byte>(512);
                var utf8 = new UTF8Encoding(false, true);
                while (true)
                {
                    int count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (count == 0) throw new IOException("Jetson closed the connection");
                    for (int i = 0; i < count; i++)
                    {
                        byte b = buffer[i];
                        if (b == 10)
                        {
                            if (line.Count > 0)
                            {
                                try { Acks.Add(new Received(utf8.GetString(line.ToArray()).TrimEnd('\r'))); }
                                catch (DecoderFallbackException) { Logs.Add("[JetBot ACK] Invalid UTF-8"); }
                            }
                            line.Clear();
                        }
                        else
                        {
                            if (line.Count >= 16384) throw new IOException("ACK line exceeds 16 KiB");
                            line.Add(b);
                        }
                    }
                }
            }

            private async Task WithDeadline(Task operation, double seconds, CancellationToken token)
            {
                using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task delay = Task.Delay(TimeSpan.FromSeconds(seconds), timer.Token);
                    if (await Task.WhenAny(operation, delay).ConfigureAwait(false) != operation)
                    {
                        CloseSocket();
                        await IgnoreFailure(operation).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("TCP operation timed out");
                    }
                    timer.Cancel();
                    await operation.ConfigureAwait(false);
                }
            }

            private static async Task IgnoreFailure(Task task)
            {
                try { await task.ConfigureAwait(false); } catch (Exception) { }
            }
        }
    }
}
