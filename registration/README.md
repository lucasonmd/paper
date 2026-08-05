# SW 저작권 등록용 자료

한국저작권위원회 컴퓨터프로그램 저작물 등록을 위해 정리한 폴더입니다.

## 등록 대상

| 구분 | 경로 | 줄 수 | 설명 |
|---|---|---:|---|
| 본체 | `src/TopicManager.Extensions/AggregationEngine.cs` | 1,177 | 집계 엔진 본체 |
| 본체 | `src/TopicManager.Extensions/JsonModuleLoader.cs` | 221 | 모듈 정의(JSON) 로더 |
| 사용 예 | `registration/UsageExample/` | 약 250 | 사용 방법을 보이는 예제 |

합계 약 1,650줄.

## UsageExample 구성

엔진을 실제로 어떻게 사용하는지 보이기 위한 최소 예제입니다.

| 파일 | 내용 |
|---|---|
| `MountTopics.cs` | 토픽 클래스 정의 (NGVA `C_Rotational_Mount` 집계를 단순화) |
| `Mount.module.json` | 토픽 종류와 참조 관계 선언 |
| `DdsMock.cs` | DDS 수신 API 대역 (`IDataReader<T>`, `CreateReader<T>`) |
| `Program.cs` | 위 셋을 엮는 코드 |

동작 흐름은 세 단계입니다.

1. `JsonModuleLoader.LoadFile()` — JSON에서 토픽 종류와 관계를 등록
2. 토픽별 DDS 리더 생성 후 각 콜백에서 `engine.Upsert()` 호출
3. `engine.SubscribeRootKind()` — 완성된 집계를 한 곳에서 수신

실행하면 등록된 토픽 종류와 생성된 리더 목록을 출력합니다.

```
> dotnet run --project registration/UsageExample

Module loaded: 6 topic kinds registered.
DDS readers created for 6 topics:
  - Mount__C_Rotational_Mount
  ...
```

## 제3자 저작물 미포함

등록 대상 전체가 자체 작성 코드이며, 외부 라이브러리 소스를 포함하지
않습니다.

- DDS 벤더 SDK를 참조하지 않습니다. `DdsMock.cs`는 수신 API의 형태만
  자체적으로 재현한 것입니다.
- NuGet 패키지를 사용하지 않습니다(.NET 8 기본 제공 라이브러리만 사용).
- 엔진 본체(`src/TopicManager.Extensions`)도 동일하게 외부 의존성이
  없습니다.

## 예제가 실제 코드와 다른 점

읽기 쉽도록 단순화한 부분이 있습니다. 등록 자료의 정확성을 위해 밝혀
둡니다.

- 토픽 키를 `long`으로 두었습니다. 실제 DDS 생성 코드는 복합 식별자
  구조체(`resourceId` + `instanceId`)를 사용하며, 엔진은 두 형태를 모두
  지원합니다.
- `DdsMock.cs`는 수신 콜백만 재현합니다. 실제 연동 시에는 벤더 리더가
  함께 주는 `SampleInfo`를 확인해야 하며(빈 샘플 제외, 다중 샘플 루프,
  인스턴스 소멸 시 `Remove` 호출), 해당 내용은 `DdsMock.cs` 주석에
  적어 두었습니다.
- 집계 수신 함수(`OnMountAggregateCompleted`)는 의도적으로 비워
  두었습니다. 애플리케이션마다 달라지는 부분이므로, 예제에서는 호출
  지점과 사용 방법만 주석으로 설명합니다.

## 저장소의 나머지 내용

등록 대상이 아닙니다. 참고용으로만 남겨 둡니다.

- `samples/` — 기능별 검증 프로그램
- `benchmarks/` — 성능 측정
- `tools/` — 모듈 정의(JSON) 작성 보조 도구
- `paper/` — 논문 원고

## 확인 필요 사항

- **권리 귀속**: 업무상 저작물이거나 국가연구개발사업 산출물인 경우
  등록 명의가 개인이 아닐 수 있습니다. 등록 전 계약·사규 확인이
  필요합니다.
- **제출 형식**: 소스코드 제출 범위와 형식은 한국저작권위원회 안내를
  따라야 합니다.
