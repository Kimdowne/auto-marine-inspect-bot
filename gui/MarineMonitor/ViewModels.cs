using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using MarineMonitor.Core;

namespace MarineMonitor;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void AllChanged() => Changed("");
}
public sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool busy;
    public bool CanExecute(object? parameter) => !busy && (canExecute?.Invoke(parameter) ?? true);
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        busy = true; Refresh();
        try { await execute(parameter); }
        finally { busy = false; Refresh(); }
    }
}
public sealed class EquipmentViewModel : Observable
{
    public string Id { get; }
    public string Title => "설비 " + Id;
    public string Source { get; private set; } = "원본 설비 연결 대기";
    public string State { get; private set; } = "데이터 대기";
    public string Simulation { get; private set; } = "—";
    public string Diagnosis { get; private set; } = "미판정";
    public string CurrentValues { get; private set; } = "—  /  —  /  —";
    public string CurrentRms { get; private set; } = "파일 RMS   —";
    public string VibrationValue { get; private set; } = "—";
    public string VibrationRms { get; private set; } = "파일 RMS   —";
    public string FileInfo { get; private set; } = "측정 기록을 기다리고 있습니다.";
    public string Error { get; private set; } = "";
    public string LastCommand { get; private set; } = "명령 대기";
    public TrendSeries CurrentTrend { get; } = new(3);
    public TrendSeries VibrationTrend { get; } = new(1);
    public AsyncCommand Control { get; }
    private EquipmentData? data;
    private bool available;
    private DateTime lastSourceStamp;
    public EquipmentViewModel(string id, Func<string, string, Task<CommandResult>> send, Action<string> log)
    {
        Id = id;
        Control = new AsyncCommand(async parameter =>
        {
            string action = (string)parameter!;
            LastCommand = action + " · 응답 대기"; Changed(nameof(LastCommand)); log($"{Title} → {action}");
            try
            {
                var result = await send(Id, action);
                LastCommand = (result.Ok ? "성공" : "실패 / 미확인") + " · " + result.Message;
                log($"{Title} {action} · {result.Code} · {result.Message}");
            }
            catch (Exception ex) { LastCommand = "명령 오류 · " + ex.Message; log(LastCommand); }
            Changed(nameof(LastCommand));
        }, parameter => available && parameter is string action && action switch
        { "pause" => data?.State == "Playing", "resume" => data?.State == "Paused", _ => true });
    }
    public void Clear(string reason, bool clearHistory = false)
    {
        available = false; State = reason; CurrentValues = "—  /  —  /  —"; VibrationValue = "—";
        CurrentRms = VibrationRms = "파일 RMS   —"; Error = "";
        CurrentTrend.Break(); VibrationTrend.Break();
        if (clearHistory) { CurrentTrend.Clear(); VibrationTrend.Clear(); data = null; lastSourceStamp = default; }
        AllChanged(); Control.Refresh();
    }
    public void Apply(EquipmentData next)
    {
        bool reset = data != null && (next.SourceEquipmentId != data.SourceEquipmentId || next.SimulationLabel != data.SimulationLabel || next.ReplaySeconds < data.ReplaySeconds);
        if (reset) { CurrentTrend.Clear(); VibrationTrend.Clear(); lastSourceStamp = default; }
        bool sourceExpired = next.State == "Playing" && (DateTime.UtcNow - next.PublishedAtUtc.ToUniversalTime()).TotalSeconds > 3;
        data = next; Source = next.SourceEquipmentId; Simulation = next.SimulationLabel;
        Diagnosis = next.Diagnosis == "NotEvaluated" ? "미판정" : next.Diagnosis;
        if (sourceExpired) { Clear("공급 데이터 만료"); return; }
        available = true;
        State = next.State switch { "Playing" => "재생 중", "Paused" => "일시정지", "Stopped" => "정지", "Completed" => "재생 완료", "Error" => "데이터 오류", _ => next.State };
        Error = next.Error;
        static string Values(double[]? values) => values == null ? "—" : string.Join("  /  ", values.Select(x => x.ToString("0.00000")));
        CurrentValues = Values(next.Current?.Values); CurrentRms = "파일 RMS   " + Values(next.Current?.FileRms);
        VibrationValue = Values(next.Vibration?.Values); VibrationRms = "파일 RMS   " + Values(next.Vibration?.FileRms);
        FileInfo = $"재생 {next.ReplaySeconds:0.0}s  ·  전류 {next.Current?.SampleRate:0}Hz / 진동 {next.Vibration?.SampleRate:0}Hz\n" +
            $"전류 기록 {next.Current?.RecordedAt:yyyy-MM-dd HH:mm:ss} · 진동 기록 {next.Vibration?.RecordedAt:yyyy-MM-dd HH:mm:ss}";
        if (next.State == "Playing" && next.PublishedAtUtc != lastSourceStamp)
        {
            if (next.Current != null && !next.Current.Completed) CurrentTrend.Add(next.Current.Values); else CurrentTrend.Break();
            if (next.Vibration != null && !next.Vibration.Completed) VibrationTrend.Add(next.Vibration.Values); else VibrationTrend.Break();
        }
        else if (next.State != "Playing") { CurrentTrend.Break(); VibrationTrend.Break(); }
        lastSourceStamp = next.PublishedAtUtc;
        AllChanged(); Control.Refresh();
    }
}
public sealed class MainViewModel : Observable
{
    private readonly MonitorConnection connection = new();
    private readonly DispatcherTimer timer;
    private ReceivedTelemetry? applied;
    private long generation = -1;
    private string port = "8765";
    public string Port { get => port; set { port = value; Changed(); } }
    public string Status { get; private set; } = "연결 안 됨";
    public string ReceiveInfo { get; private set; } = "마지막 수신 —";
    public string FrameInfo { get; private set; } = "수신 대기";
    public string ConnectionColor { get; private set; } = "#71859D";
    public string PreviewNotice { get; set; } = "실시간 시뮬레이터 데이터 · 진단 미연결";
    public string ConnectLabel => connection.IsRunning ? "연결 해제" : "Unity 연결";
    public bool CanEditPort => !connection.IsRunning;
    public ObservableCollection<EquipmentViewModel> Equipment { get; }
    public ObservableCollection<ClientEvent> Events { get; } = new();
    public AsyncCommand Connect { get; }
    public MainViewModel()
    {
        Equipment = new(new[] { new EquipmentViewModel("A", connection.SendAsync, Log), new EquipmentViewModel("B", connection.SendAsync, Log) });
        Connect = new AsyncCommand(async _ =>
        {
            if (connection.IsRunning) await connection.DisconnectAsync();
            else if (int.TryParse(Port, out int number) && number is >= 1 and <= 65535) { generation = -1; connection.Connect(number); Log("연결 요청 · localhost:" + number); }
            else Log("포트는 1~65535 사이의 숫자로 입력하세요.");
            AllChanged();
        });
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Log("앱 준비 완료 · Unity 전용 씬을 재생한 뒤 연결하세요.");
    }
    public void Log(string message)
    {
        Events.Insert(0, new ClientEvent(DateTime.Now, message));
        while (Events.Count > 150) Events.RemoveAt(Events.Count - 1);
    }
    private void Refresh()
    {
        while (connection.TryReadEvent(out var item)) if (item != null) Log(item.Message);
        Status = connection.Status;
        var latest = connection.Latest;
        bool fresh = latest != null && (DateTime.UtcNow - latest.ReceivedAtUtc).TotalSeconds < 2;
        ConnectionColor = fresh && connection.IsConnected ? "#46D9C1" : "#E9B15C";
        if (fresh && connection.IsConnected)
        {
            ReceiveInfo = $"마지막 수신 {latest!.ReceivedAtUtc.ToLocalTime():HH:mm:ss}  ·  {(DateTime.UtcNow - latest.ReceivedAtUtc).TotalSeconds:0.0}s 전";
            FrameInfo = $"FRAME {latest.Data.Sequence:N0}  /  약 10Hz";
            if (generation != latest.ConnectionId) { foreach (var e in Equipment) e.Clear("연결됨", true); generation = latest.ConnectionId; }
            if (!ReferenceEquals(latest, applied))
            {
                foreach (var e in Equipment)
                {
                    var data = latest.Data.Equipment.FirstOrDefault(x => x.EquipmentId == e.Id);
                    if (data == null) e.Clear("설비 데이터 없음");
                    else
                    {
                        if (e.Error != data.Error && !string.IsNullOrEmpty(data.Error)) Log($"설비 {e.Id} 오류 · {data.Error}");
                        e.Apply(data);
                    }
                }
                applied = latest;
            }
        }
        else
        {
            if (connection.IsConnected) Status = "데이터 수신 대기 / 만료";
            ReceiveInfo = applied == null ? "마지막 수신 —" : $"마지막 수신 {applied.ReceivedAtUtc.ToLocalTime():HH:mm:ss} · 현재값 만료";
            foreach (var e in Equipment) e.Clear(connection.IsConnected ? "데이터 만료 / 대기" : "연결 끊김");
        }
        foreach (var e in Equipment) { e.CurrentTrend.Pulse(); e.VibrationTrend.Pulse(); }
        AllChanged();
    }
    public async Task CloseAsync() { timer.Stop(); await connection.DisconnectAsync(); }
    public void LoadPreview()
    {
        timer.Stop(); PreviewNotice = "UI 미리보기 · 합성 데이터 · 실제 측정/진단 아님"; Status = "미리보기"; FrameInfo = "PREVIEW"; ReceiveInfo = "합성 데이터";
        for (int j = 0; j < 2; j++)
        {
            Equipment[j].Apply(new EquipmentData { EquipmentId = j == 0 ? "A" : "B", SourceEquipmentId = j == 0 ? "L-DSF-01" : "L-SF-04", SimulationLabel = j == 0 ? "정상" : "베어링불량", Diagnosis = "NotEvaluated", State = "Playing", PublishedAtUtc = DateTime.UtcNow, ReplaySeconds = 24.6,
                Current = new SignalData { Values = [2.13, 2.45, 2.21], FileRms = [2.29, 2.11, 2.45], SampleRate = 2000, RecordedAt = new DateTime(2020, 11, 25, 14, 12, 45) },
                Vibration = new SignalData { Values = [0.0058], FileRms = [0.0042], SampleRate = 4000, RecordedAt = new DateTime(2020, 11, 25, 14, 12, 45) } });
            Equipment[j].CurrentTrend.Clear(); Equipment[j].VibrationTrend.Clear();
            for (int i = 0; i < 500; i++)
            {
                var time = DateTime.UtcNow.AddSeconds(-50 + i * .1);
                Equipment[j].CurrentTrend.Add([Math.Sin(i * .12) * .8 + 2, Math.Cos(i * .1) * .7 + 2, Math.Sin(i * .13 + 1) * .6 + 2], time);
                Equipment[j].VibrationTrend.Add([Math.Sin(i * .37) * (j == 0 ? .004 : .009)], time);
            }
        }
        Log("정상·고장 설정은 CSV 라벨이며 진단 결과와 별개입니다."); AllChanged();
    }
}
