# Equipment monitoring prototype

외부 TCP 통신과 콘솔 실행 방법은 [NETWORK.md](NETWORK.md)를 참고하세요. 전용 씬에서 기본적으로 localhost:8765 통신을 시작합니다.

## 실행

1. Unity의 컴파일이 완료되면 `Tools > Ship Robot > Open Equipment Monitoring Prototype`을 선택합니다.
2. 현재 씬의 변경사항을 저장한 후 전용 씬에서 Play를 누릅니다.
3. 기본값: A = L-DSF-01 정상, B = L-SF-04 베어링불량. 각 패널에서 정상/고장 전환, 일시정지, 재개, 재시작, 정지를 사용할 수 있습니다.
4. 재생 전 `Equipment Monitoring Prototype` 오브젝트의 Inspector에서 원본 설비, 용량, 고장 라벨, 시작 상태를 변경할 수 있습니다. 실행 중 배정을 수정하려면 Play를 종료하고 변경합니다.

기존 로봇 씬, 구동/학습 코드, Build Settings는 수정하지 않습니다. 이 씬은 로봇 없이 실행됩니다.

## 데이터 위치와 의미

기본 데이터 루트는 프로젝트 루트의 `data`입니다. 절대 경로도 지원합니다. 빌드에서는 실행 파일 옆의 `data`를 찾으므로 데이터를 별도로 배포하거나 Inspector에 경로를 지정해야 합니다. 원본 약 870MB를 Resources에 넣거나 자동으로 빌드에 포함하지 않습니다.

폴더 구조: `{root}/{current|vibration}/{capacity}/{sourceEquipmentId}/{label}/*.csv`.

- 전류: 시간 + CH1/CH2/CH3, 진동: 시간 + CH1. 채널별 단위는 원본 설명 확인 전까지 지정하지 않습니다.
- 원본 측정 시각은 시간대 미지정 DateTime, 이벤트 발행 시각은 UTC, 누적 재생 시간은 시뮬레이션 경과 초입니다.
- 전류와 진동은 별개의 기록입니다. 파일명 순으로 각각 재생하며 동기 측정으로 간주하지 않습니다.
- 파일 RMS는 원본 메타데이터 값입니다. 실시간 이동 구간 RMS가 아닙니다.
- CSV 라벨은 시뮬레이션 상태입니다. `Diagnosis = NotEvaluated`이며 자동 진단은 구현하지 않습니다.
- 재생은 Unity `Time.deltaTime`을 따릅니다. timeScale=0이면 멈춥니다. 기본 발행 주기는 약 10Hz이며 원본 주파수에 따라 해당 시각의 샘플을 선택합니다. 전체 파형을 GUI로 전송하는 계약은 아닙니다.

## 구조와 교체 지점

`CsvSignal`은 9행의 메타데이터와 숫자 파형을 엄격히 검증합니다. 현재 데이터의 쉼표 구분 형식을 대상으로 하며 임의의 따옴표 CSV 형식을 지원하는 범용 파서는 아닙니다.

`CsvReplaySession`은 Unity 독립 C# 재생 엔진입니다. 현재 파일만 메모리에 유지하고 파일 경계에서 다음 파일을 읽습니다. 파일 읽기는 동기식이므로 향후 고빈도/다수 설비 환경에서는 비동기 사전 로딩을 추가할 수 있습니다.

`CsvReplayDataSource`는 Unity 생명주기/재생 제어 어댑터입니다. `IEquipmentDataSource.Latest`와 `SnapshotChanged`가 모니터링/통신의 공통 입력입니다. 향후 로봇 데이터 공급원을 같은 인터페이스로 연결할 수 있습니다. 이벤트는 Unity 메인 스레드에서 발생합니다. 백그라운드 네트워크 전송 시 큐를 사용하고 Unity API를 작업 스레드에서 호출하지 않습니다.

`EquipmentMonitorPanel`의 데이터 표시는 인터페이스만 참조합니다. CSV 전용 버튼은 공급원이 CSV일 때만 표시합니다. 향후 진단 계약은 별도 결과 모델로 추가하고 CSV 상태 라벨을 진단 결과로 사용하지 않습니다.

`EquipmentMonitoringPrototype`은 독립 씬 구성기입니다. 기존 씬에 통합할 때는 필요한 데이터 공급원/패널만 연결하고 로봇 제어와 분리합니다.

상태 전환은 재생 시간을 0으로 초기화합니다. 오류/정지 시 측정값을 비워 오래된 값이 실시간값처럼 표시되지 않게 합니다. 비반복 재생은 각 스트림의 마지막 샘플과 완료 플래그를 유지하며, 두 스트림이 끝나면 전체 완료입니다. 비활성화 시 정지하며 재활성화 후 재시작으로 복구합니다.

## 검증

프로젝트 루트에서 .NET 9 SDK 이상으로 실행:

```powershell
dotnet run --project tools/EquipmentMonitoring.Tests -- data
```

CSV 전체를 실제 런타임 로더로 검사하고 별도 임시 파일로 재생 시각, 일시정지/재개, 전류·진동 독립 순환, 비반복 완료, 오류/정지 시 값 제거, 경로 검증, 다음 파일 손상을 검사합니다. 원본 데이터는 변경하지 않습니다.

Unity 참조 DLL을 사용한 컴파일 검사도 가능합니다:

```powershell
dotnet run --project tools/EquipmentMonitoring.Tests -p:UnityManagedPath="C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/Managed" -- data
```

Unity 수동 확인: 전용 씬 Play → A/B 값 갱신 → A만 일시정지 시 B 계속 재생 → A 재개 → 상태 변경 시 시간 초기화 → 정지 시 값 제거. 잘못된 데이터 경로를 설정하면 오류와 데이터 없음이 표시되어야 합니다.
