using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ShipRobot.EquipmentMonitoring
{
    // Loopback-only, one client. All socket work stays off Unity's main thread.
    public sealed class EquipmentTcpServer : IDisposable
    {
        private sealed class Connection
        {
            public long Id;
            public TcpClient Client;
            public volatile bool Alive = true;
            public readonly ConcurrentQueue<string> Responses = new ConcurrentQueue<string>();
        }
        private sealed class Request { public Connection Connection; public string Json; }
        private readonly ConcurrentQueue<Request> requests = new ConcurrentQueue<Request>();
        private readonly object gate = new object();
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private Connection active;
        private string latest;
        private long nextId;
        public int Port { get; private set; }
        public bool Connected { get { lock (gate) return active != null && active.Alive; } }
        public string LastError { get; private set; } = "";
        public void Start(int port)
        {
            if (running) throw new InvalidOperationException("Already listening");
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(1); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Equipment TCP accept" };
            acceptThread.Start();
        }
        public void Publish(string json) { lock (gate) latest = json; }
        // Called by Unity Update. Commands from disconnected clients are discarded.
        public void Pump(Func<long, string, string> handler, int budget = 8)
        {
            for (int i = 0; i < budget && requests.TryDequeue(out Request request); i++)
            {
                if (!request.Connection.Alive) continue;
                string response = handler(request.Connection.Id, request.Json);
                if (!request.Connection.Alive) continue;
                if (request.Connection.Responses.Count >= 64) Close(request.Connection);
                else request.Connection.Responses.Enqueue(response);
            }
        }
        private void AcceptLoop()
        {
            while (running)
            {
                Connection connection = null;
                try
                {
                    var client = listener.AcceptTcpClient();
                    client.NoDelay = true; client.SendTimeout = 2000;
                    connection = new Connection { Id = ++nextId, Client = client };
                    lock (gate) active = connection;
                    var captured = connection;
                    var writer = new Thread(() => WriteLoop(captured)) { IsBackground = true, Name = "Equipment TCP send" };
                    writer.Start();
                    using (var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false, true), false, 1024, true))
                    {
                        while (running && connection.Alive)
                        {
                            string line = ReadBoundedLine(reader);
                            if (line == null) break;
                            if (requests.Count >= 64) throw new IOException("Command queue full");
                            requests.Enqueue(new Request { Connection = connection, Json = line });
                        }
                    }
                    Close(connection); writer.Join(2500);
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is DecoderFallbackException)
                { if (running) LastError = ex.Message; }
                finally
                {
                    if (connection != null) Close(connection);
                    lock (gate) if (active == connection) active = null;
                }
            }
        }
        private static string ReadBoundedLine(TextReader reader)
        {
            var line = new StringBuilder();
            while (true)
            {
                int value = reader.Read();
                if (value < 0) return null; // Never execute a truncated command.
                if (value == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= 4096) throw new IOException("Command exceeds 4096 characters");
                line.Append((char)value);
            }
        }
        private void WriteLoop(Connection connection)
        {
            try
            {
                using var writer = new StreamWriter(connection.Client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
                string sent = null;
                while (running && connection.Alive)
                {
                    while (connection.Responses.TryDequeue(out string response)) writer.WriteLine(response);
                    string snapshot; lock (gate) snapshot = latest;
                    if (snapshot != null && !ReferenceEquals(snapshot, sent)) { writer.WriteLine(snapshot); sent = snapshot; }
                    Thread.Sleep(20);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            { if (running) LastError = ex.Message; }
            finally { Close(connection); }
        }
        private static void Close(Connection connection) { connection.Alive = false; connection.Client.Close(); }
        public void Dispose()
        {
            running = false; listener?.Stop();
            lock (gate) if (active != null) Close(active);
            acceptThread?.Join(500);
            while (requests.TryDequeue(out _)) { }
        }
    }
}
