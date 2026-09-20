# 외부 통신 프로토타입 (v1)

## 실행

1. Unity에서 `Tools > Ship Robot > Open Equipment Monitoring Prototype` → Play.
2. 패널에 `TCP 127.0.0.1:8765 · 연결 대기`가 표시되는지 확인합니다.
3. 별도 PowerShell에서 프로젝트 루트로 이동하고 실행합니다 (.NET 9 런타임 및 호환 SDK 필요).

```powershell
dotnet run --project tools/EquipmentMonitorClient
```

콘솔은 A/B의 최신 값을 1초 간격으로 요약 출력합니다. 아래 명령을 입력할 수 있습니다.

```text
A pause
A resume
B normal
B fault
A restart
B stop
quit
```

재생 명령은 테스트용입니다. 로봇 임무 하달은 아직 연결하지 않았습니다. Unity의 정상/고장 버튼과 같은 재생기를 조작합니다. pause는 Playing, resume는 Paused에서만 허용되며 오류/미등록 설비/알 수 없는 동작은 실패 응답을 반환합니다.

Unity Play를 종료하면 콘솔은 연결 끊김을 표시하고 2초 간격으로 재연결합니다. 다시 Play하면 데이터 수신이 재개됩니다. 5초간 메시지가 없을 때도 연결을 끊고 재연결합니다. 연결이 없을 때 명령은 큐에 저장하지 않습니다. 명령 응답이 5초 내에 없거나 연결이 끊기면 처리 여부 미확인으로 표시하며 자동 재전송하지 않습니다.

포트는 씬의 `Equipment Monitoring Prototype / Network Port`에서 변경합니다. 콘솔에는 `dotnet run --project tools/EquipmentMonitorClient -- 8766`처럼 전달합니다. 포트 충돌 시 Unity 로그와 패널에 오류가 표시되며 데이터 재생은 계속됩니다. 통신을 끄려면 Play 전에 Enable Network를 해제합니다.

## 전송 계약

TCP, UTF-8 (BOM 없음), JSON 한 줄 + LF. 읽기 1회가 메시지 1개라는 보장이 없으므로 줄 경계까지 모아 파싱해야 합니다. 현재 **동일 PC, loopback 주소, 동시 클라이언트 1개**를 지원합니다. 인증/TLS/LAN 접속은 구현하지 않았으며 외부 인터페이스에 바인딩하지 않습니다.

스냅샷 예시의 `equipment`에는 `EquipmentSnapshot`의 camelCase 필드가 들어갑니다.

```json
{"version":1,"type":"telemetry","sequence":1,"sentAtUtc":"2026-09-12T12:00:00Z","equipment":[]}
```

- 약 10Hz로 A/B 최신 스냅샷을 전달합니다. 느린 클라이언트에는 중간 프레임을 생략하므로 전체 샘플 기록용이 아닙니다.
- `equipmentId`, `sourceEquipmentId`, `simulationLabel`, `diagnosis`, `state`, `publishedAtUtc`, `replaySeconds`, `current`, `vibration`, `error`.
- 각 신호: `fileName`, `recordedAt`, `positionSeconds`, `durationSeconds`, `sampleRate`, `values`, `fileRms`, `completed`.
- `state`는 Playing / Paused / Stopped / Completed / Error 문자열입니다. 정지·오류 시 current/vibration은 null입니다.
- `simulationLabel`은 시뮬레이션 정답 설정, `diagnosis`는 NotEvaluated입니다. 이를 진단 결과로 바꾸어 해석하지 않습니다.
- `sentAtUtc`는 전송 생성 시각, `publishedAtUtc`는 공급원 스냅샷 생성 시각입니다. 정지·일시정지 상태에서는 공급원 시각이 과거여도 전송은 계속됩니다. 원본 `recordedAt`에는 시간대 정보가 없습니다.
- 재생은 시뮬레이션 시간, 네트워크 발행은 unscaled 시간입니다. 에디터 자체 Pause 상태에서는 Update가 멈추므로 새 전송도 멈춥니다.
- sequence는 브리지 생명주기 내 증가합니다. 새 Play 세션에서 초기화될 수 있습니다.

```json
{"version":1,"type":"command","commandId":"unique-id","equipmentId":"A","action":"pause"}
{"version":1,"type":"commandResult","commandId":"unique-id","ok":true,"code":"ok","message":"Command applied"}
```

동작: pause / resume / restart / stop / normal / fault. 실패 코드는 invalid_command / invalid_protocol / invalid_json / id_conflict / rejected / execution_error입니다. 손상된 JSON에서는 commandId가 null일 수 있습니다.

명령 ID는 1~80자입니다. 연결별 최근 128개 명령의 응답을 캐시하여 **동일 ID + 동일 JSON 문자열** 재수신 시 재실행하지 않습니다. 같은 ID로 다른 문자열을 보내면 id_conflict입니다. 연결이 바뀌거나 캐시에서 밀려나면 중복 방지가 보장되지 않습니다. 재연결 후 미확인 명령은 최신 상태를 확인하고 사용자가 다시 판단해야 합니다.

명령 1줄은 최대 4,096문자, 수신 대기열/응답 대기열은 각각 64개입니다. 제한 초과와 잘못된 UTF-8은 연결을 종료합니다. 부분 메시지는 LF가 도착하기 전까지 실행하지 않습니다. 끊어진 연결의 대기 명령은 폐기합니다. 이미 실행을 시작한 명령의 취소는 보장하지 않습니다.

## 구현 경계

- `EquipmentTcpServer`: 백그라운드 TCP 수신/송신, 스냅샷 병합, 명령 큐.
- `EquipmentWireProtocol`: v1 JSON 변환. 이미 프로젝트에 설치된 Newtonsoft.Json을 사용합니다.
- `EquipmentCommandRouter`: 형식 검증, 중복 처리, 명령 처리 함수 호출. 향후 로봇 명령 핸들러를 연결할 지점입니다.
- `EquipmentNetworkBridge`: Unity Update에서만 공급원과 재생 제어를 호출합니다. 통신 스레드는 Unity API를 호출하지 않습니다.
- `tools/EquipmentMonitorClient`: Unity 의존성이 없는 .NET 콘솔. 향후 WPF에서도 같은 계약으로 연결할 수 있습니다.

## 자동 검증

```powershell
dotnet build tools/EquipmentMonitorClient
dotnet run --project tools/EquipmentNetwork.Tests -p:UnityManagedPath="C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/Managed" -- tools/EquipmentMonitorClient/bin/Debug/net9.0/EquipmentMonitorClient.dll
```

실제 loopback 소켓과 CSV 재생 엔진으로 A/B 수신, 한글 라벨, pause/resume 응답·상태, JSON 오류, 중복 ID, 과대 메시지, 재연결, 포트 충돌을 검사합니다. 마지막에는 외부 콘솔을 별도 프로세스로 실행해 수신·명령·응답·종료를 검사합니다. Unity 참조 DLL로 어댑터까지 컴파일하지만, Unity 에디터 Play에서의 수동 통합 검증을 대체하지는 않습니다.
