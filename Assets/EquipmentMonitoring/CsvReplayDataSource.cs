using System;
using System.IO;
using UnityEngine;

namespace ShipRobot.EquipmentMonitoring
{
    [DisallowMultipleComponent]
    public sealed class CsvReplayDataSource : MonoBehaviour, IEquipmentDataSource
    {
        [SerializeField] private string equipmentId = "A";
        [Tooltip("Absolute path, or relative to the Unity project / player folder. Data is not bundled automatically.")]
        [SerializeField] private string dataRoot = "data";
        [SerializeField] private string capacity = "2.2kW";
        [SerializeField] private string sourceEquipmentId = "L-DSF-01";
        [SerializeField] private string faultLabel = "축정렬불량";
        [SerializeField] private bool startWithFault;
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private bool loop = true;
        [SerializeField, Range(1, 30)] private int publishRateHz = 10;
        private CsvReplaySession session;
        private double pendingSeconds;
        private bool selectedFault;
        public EquipmentSnapshot Latest => session?.Latest;
        public event Action<EquipmentSnapshot> SnapshotChanged;
        public string FaultLabel => faultLabel;

        public void Configure(string id, string root, string power, string source, string fault, bool initiallyFaulted)
        {
            if (session != null) throw new InvalidOperationException("Configure before Start");
            equipmentId = id; dataRoot = root; capacity = power; sourceEquipmentId = source;
            faultLabel = fault; startWithFault = initiallyFaulted;
        }
        private void Start()
        {
            EnsureSession(); selectedFault = startWithFault;
            if (playOnStart) Restart();
        }
        private void EnsureSession()
        {
            if (session != null) return;
            session = new CsvReplaySession(equipmentId);
            session.SnapshotChanged += ForwardSnapshot;
        }
        private void ForwardSnapshot(EquipmentSnapshot snapshot) => SnapshotChanged?.Invoke(snapshot);
        private void Update()
        {
            if (session == null || session.Latest.State != DataSourceState.Playing) return;
            pendingSeconds += Time.deltaTime;
            if (pendingSeconds < 1d / Mathf.Max(1, publishRateHz)) return;
            double delta = pendingSeconds; pendingSeconds = 0;
            session.Tick(delta);
        }
        public void SelectState(bool fault) { selectedFault = fault; Restart(); }
        public void Restart()
        {
            EnsureSession(); pendingSeconds = 0;
            string root = Path.IsPathRooted(dataRoot) ? dataRoot : Path.Combine(Application.dataPath, "..", dataRoot);
            session.Start(root, capacity, sourceEquipmentId, selectedFault ? faultLabel : "정상", loop);
        }
        public void Pause() { EnsureSession(); session.Tick(pendingSeconds); pendingSeconds = 0; session.Pause(); }
        public void Resume() { EnsureSession(); session.Resume(); }
        public void Stop() { pendingSeconds = 0; session?.Stop(); }
        private void OnDisable() => Stop();
        private void OnDestroy() { if (session != null) session.SnapshotChanged -= ForwardSnapshot; }
    }
}
