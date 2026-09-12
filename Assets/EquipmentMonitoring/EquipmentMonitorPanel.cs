using System.Globalization;
using UnityEngine;

namespace ShipRobot.EquipmentMonitoring
{
    public sealed class EquipmentMonitorPanel : MonoBehaviour
    {
        [Tooltip("Any MonoBehaviour implementing IEquipmentDataSource can be monitored.")]
        [SerializeField] private MonoBehaviour[] sources;
        private Vector2 scroll;
        private GUIStyle title, text, button;
        private Font font;
        public void SetSources(params MonoBehaviour[] providers) => sources = providers;
        private void OnGUI()
        {
            if (text == null)
            {
                font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "Arial" }, 16);
                title = new GUIStyle(GUI.skin.label) { font = font, fontSize = 22, fontStyle = FontStyle.Bold };
                text = new GUIStyle(GUI.skin.label) { font = font, fontSize = 14, wordWrap = true };
                button = new GUIStyle(GUI.skin.button) { font = font, fontSize = 14 };
            }
            GUILayout.BeginArea(new Rect(16, 16, Mathf.Max(240, Screen.width - 32), Mathf.Max(180, Screen.height - 32)), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("설비 데이터 모니터링", title);
            GUILayout.Label("CSV 재생 프로토타입 · 로봇 연결 없음 · 진단 미판정", text);
            var network = GetComponent<EquipmentNetworkBridge>();
            if (network != null) GUILayout.Label(network.Status, text);
            if (sources != null) foreach (MonoBehaviour provider in sources)
            {
                if (!(provider is IEquipmentDataSource source)) continue;
                EquipmentSnapshot snapshot = source.Latest;
                GUILayout.BeginVertical(GUI.skin.box);
                if (snapshot == null) GUILayout.Label("데이터 공급원 초기화 중", text);
                else
                {
                    GUILayout.Label($"설비 {snapshot.EquipmentId}  /  {snapshot.SourceEquipmentId}", title);
                    GUILayout.Label($"재생 상태: {snapshot.State}    누적 재생: {snapshot.ReplaySeconds:F1}s", text);
                    GUILayout.Label($"시뮬레이션 설정: {snapshot.SimulationLabel}    진단: 미판정", text);
                    if (!string.IsNullOrEmpty(snapshot.Error)) GUILayout.Label("데이터 오류: " + snapshot.Error, text);
                    DrawSignal("전류", snapshot.Current);
                    DrawSignal("진동", snapshot.Vibration);
                }
                // Optional playback controls; monitoring itself only depends on the interface.
                if (provider is CsvReplayDataSource replay)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("정상", button, GUILayout.Height(30))) replay.SelectState(false);
                    if (GUILayout.Button(replay.FaultLabel, button, GUILayout.Height(30))) replay.SelectState(true);
                    if (GUILayout.Button("일시정지", button, GUILayout.Height(30))) replay.Pause();
                    if (GUILayout.Button("재개", button, GUILayout.Height(30))) replay.Resume();
                    if (GUILayout.Button("재시작", button, GUILayout.Height(30))) replay.Restart();
                    if (GUILayout.Button("정지", button, GUILayout.Height(30))) replay.Stop();
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
            GUILayout.Label("전류·진동은 독립된 기록입니다. RMS는 파일 전체 구간 값이며 물리 단위는 원본 설명 확인 전까지 지정하지 않습니다.", text);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        private void DrawSignal(string name, SignalSnapshot signal)
        {
            if (signal == null) { GUILayout.Label(name + ": 현재 데이터 없음", text); return; }
            string channels = "";
            for (int i = 0; i < signal.Values.Count; i++)
                channels += $"  CH{i + 1} {signal.Values[i].ToString("F5", CultureInfo.InvariantCulture)} (파일 RMS {signal.FileRms[i].ToString("F5", CultureInfo.InvariantCulture)})";
            GUILayout.Label(name + channels, text);
            GUILayout.Label($"원본 측정: {signal.RecordedAt:yyyy-MM-dd HH:mm:ss} (시간대 미지정) | {signal.PositionSeconds:F3}/{signal.DurationSeconds:F3}s | {signal.SampleRate:F0}Hz{(signal.Completed ? " | 재생 완료" : "")}", text);
            GUILayout.Label(signal.FileName, text);
        }
        private void OnDestroy() { if (font != null) Destroy(font); }
    }
}
