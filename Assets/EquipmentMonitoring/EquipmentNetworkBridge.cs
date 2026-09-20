using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShipRobot.EquipmentMonitoring
{
    public sealed class EquipmentNetworkBridge : MonoBehaviour
    {
        [SerializeField] private int port = 8765;
        [SerializeField] private MonoBehaviour[] sources;
        private EquipmentTcpServer server;
        private readonly EquipmentCommandRouter router = new EquipmentCommandRouter();
        private float nextPublish;
        private long sequence;
        private string startupError;
        public string Status => server == null ? "통신 중지: " + startupError : $"TCP 127.0.0.1:{server.Port} · " + (server.Connected ? "외부 클라이언트 연결됨" : "연결 대기");
        public void Configure(int listenPort, params MonoBehaviour[] providers) { port = listenPort; sources = providers; }
        private void Start() => StartServer();
        private void OnEnable() { if (started) StartServer(); }
        private bool started;
        private void StartServer()
        {
            started = true;
            if (server != null) return;
            try { server = new EquipmentTcpServer(); server.Start(port); startupError = ""; }
            catch (Exception ex) { server?.Dispose(); server = null; startupError = ex.Message; Debug.LogError("Equipment TCP: " + ex.Message, this); }
        }
        private void Update()
        {
            if (server == null) return;
            server.Pump((connection, json) => router.Handle(connection, json, ExecuteReplayCommand));
            if (Time.unscaledTime < nextPublish) return;
            nextPublish = Time.unscaledTime + 0.1f;
            var values = new List<EquipmentSnapshot>();
            if (sources != null) foreach (MonoBehaviour source in sources)
                if (source != null && source is IEquipmentDataSource provider && provider.Latest != null) values.Add(provider.Latest);
            server.Publish(EquipmentWireProtocol.Telemetry(++sequence, values));
        }
        private string ExecuteReplayCommand(string id, string action)
        {
            CsvReplayDataSource target = null;
            if (sources != null) foreach (MonoBehaviour source in sources)
                if (source is CsvReplayDataSource replay && replay.Latest?.EquipmentId == id)
                { if (target != null) return "Ambiguous equipment ID"; target = replay; }
            if (target == null) return "Unknown or unsupported equipment ID";
            if (!target.isActiveAndEnabled) return "Equipment source is disabled";
            switch (action)
            {
                case "pause": if (target.Latest.State != DataSourceState.Playing) return "Expected Playing"; target.Pause(); break;
                case "resume": if (target.Latest.State != DataSourceState.Paused) return "Expected Paused"; target.Resume(); break;
                case "restart": target.Restart(); break;
                case "stop": target.Stop(); break;
                case "normal": target.SelectState(false); break;
                case "fault": target.SelectState(true); break;
                default: return "Unsupported action";
            }
            return target.Latest.State == DataSourceState.Error ? target.Latest.Error : null;
        }
        private void OnDisable() { server?.Dispose(); server = null; }
    }
}
