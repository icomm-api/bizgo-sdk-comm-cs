# Changelog

이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

## 1.2.0 - 2026-09-28

첫 공개 릴리스. 비즈고 커뮤니케이션 API(`https://mars.ibapi.kr`) 기준으로 새로 작성했습니다.
비즈고 SDK 6종과 버전을 맞춰 1.2.0으로 시작합니다(그 이전 버전은 배포하지 않았습니다).
이전 `Infobank.Omni.Sdk.Cs`(OMNI API)와 호환되지 않으며, NuGet 패키지 이름도 `Bizgo.Sdk.Comm`으로 바뀌었습니다.
테스트 도구는 별도 패키지 `Bizgo.Sdk.Comm.Testing`(같은 버전)으로 배포합니다.

### 클라이언트
- `BizgoClient`(netstandard2.0, net8.0), API Key 인증(환경변수 `BIZGO_API_KEY`, `^[\x21-\x7e]+$`만 허용, 공백을 잘라내지 않음), 운영·sandbox 환경
- SDK가 `HttpClient`를 직접 만들고 소유(시도당 타임아웃, 연결 타임아웃). 프록시·`IHttpClientFactory`는 `HttpMessageHandler`로 넘기며, 자체 인증(`Credentials` 등)·`AllowAutoRedirect`·`UseCookies`를 쓰는 핸들러는 설정 오류
- SDK 식별 헤더 `User-Agent`(`bizgo-sdk-comm-cs/<ver> dotnet/<ver> (<os>; <arch>)`, 선택 `AppInfo`)와 `X-Bizgo-Client`
- 재시도(429·5xx·네트워크, 발송은 멱등성 키가 있을 때만, `Retry-After`는 0~60초의 유한한 값만), 3xx는 따르거나 재시도하지 않음, 재시도 뒤 `A301`이면 `AlreadyAccepted`
- 클라이언트 속도 제한(`RateLimit`): send 버킷(`x-sdk-rate: send`, 초당 200 메시지, 수신자 수 기준) / other 버킷(초당 5 요청), 가득 찬 상태로 시작
- 관측: `Hooks`(operation·경로 템플릿·상태·코드·시도·소요 시간만, async 콜백 거부, 예외는 삼킴), net8.0에서 `ActivitySource("Bizgo")` span

### API
- 통합 발송 `Send.OmniAsync()`와 간편 메서드 `SmsAsync`/`LmsAsync`/`MmsAsync`, 대체발송, 치환, 멱등성 키
- 대량 발송 `Send.BulkAsync`(청크, 동시성, `<prefix>-<chunkSize>-<start>-<hash8>` 멱등성 키, 청크별 실패 격리)
- 수신자별 `A301`(같은 멱등성 키 재발송, HTTP 200, sandbox 2026-09-28 확인)은 실패가 아닌 `SendResult.Duplicates`/`BulkSendResult.Duplicates`(앞 요청에서 이미 접수된 수신자)로 돌려주며 `Succeeded`에도 넣지 않습니다. 모두 `A301`인 청크도 오류가 아닙니다. 요청 단계 `A301`은 `BizgoDuplicateRequestException`. 테스트 도구 `FakeResponder.SendDuplicates()`
- `idempotencyKey`만 지정하고 `idempotencyTtl`을 비워 두면 SDK가 `idempotencyTtl`=`BizgoClient.DefaultIdempotencyTtl`(공개 상수, 86400초)을 채워 보냅니다(비워 보내면 비즈고가 `A309`로 거절, sandbox 2026-09-28 확인). 직접 지정한 값(0 포함)은 그대로 보내고, 키가 없으면 TTL을 더하지 않습니다. `Send.*Async`, `Send.RequestAsync`(넘긴 요청 객체는 바꾸지 않음), `BulkAsync` 청크(`IdempotencyKeyPrefix`), 본문에 두 필드가 모두 있는 생성 operation에 적용
- 이미지 업로드(MMS 300KB 검사, RCS, 브랜드메시지), 리포트 polling(`ConsumeAsync`)·수신 확인·개별 조회, 상태·이력·MO 이력(`IAsyncEnumerable`)·통계
- 스펙의 operation 146개 전체를 `x-sdk-*` 메타데이터로 생성한 리소스 메서드로 제공(`Reservations`, `Insights`, `Kakao`, `Alimtalk`, `BrandMessage`, `Rcs`, `Counsel`, `Files`): 경로·본문·헤더·쿼리 인자, `x-sdk-result` 반환(null 없음), `x-sdk-retry`, `Enumerate...Async` 페이지 순회, multipart 업로드. `BizgoOperation`으로 operation 메타데이터 조회
- 채널·응답 모델을 OpenAPI 스펙에서 생성(System.Text.Json 소스 생성), snake_case JSON 필드는 PascalCase 속성
- `AlimtalkTemplate.MsgType`(응답, 알려진 값 `AT`/`AI`, 문자열): 전문 발송의 `AlimtalkMessage.MsgType`에 그대로 쓸 수 있습니다

### 검증과 오류
- 보내기 전 검증: 필수 필드, 길이, 바이트 길이(EUC-KR/CP949), 허용 값, 패턴, 범위, 수신자 200명, 채널 키 1개, 알 수 없는 필드
- 조건부 필수 검사(`x-sdk-required-if`): 생성기가 스펙 규칙으로 보내기 전 검사를 만듭니다. 알림톡 전문 발송(`sendType` 없음)에 `msgType`(`AT`/`AI`)·`text`가 없으면 서버 `A523` 대신 `BizgoValidationException`(sandbox 2026-09-28 확인), 템플릿 자동 치환 발송(`sendType = template`)은 모든 `destinations[].replaceWords` 필수(`msgType` 불필요). 브랜드메시지 `sendType`별 필드·버튼 `WL`/`AL` URL, RCS `header = 1`의 `footer`, 상담톡 Plain/Rich 메시지 타입별 첨부도 검사합니다. 요청 본문 기준(`$.`) 규칙은 요청 전체를 검증할 때 검사하며, 오류에는 필드 경로와 조건만 담습니다
- 오류 계층(게이트웨이/상품 단계 구분, 에러코드 설명), 응답 파싱 실패는 `BizgoInvalidResponseException`(상태·`TrackingId`·`Body`, 발송이면 "접수됐을 수 있음"), 응답 16MB(압축 해제 후)·JSON 64단계 제한
- `BizgoApiException.ToJson()`/`FromJson()`(예외는 `[Serializable]`이 아님)
- 모델·옵션 `ToString()`: 전화번호 마스킹, 이름·닉네임 등 개인정보 마스킹, 본문은 길이만
- 업로드 파일 경로 오류는 파일 이름만 담은 `BizgoValidationException`

### 웹훅
- 서명 검증(hex/base64, constant-time, `X-IB-Timestamp`는 공백 없는 ASCII 숫자 1~16자리), 리포트·MO·상담톡 7종 파싱, JSON 오류·깊은 중첩은 `BizgoWebhookVerificationException`
- 상담톡 웹훅: 서명이 없으므로(서명은 리포트·MO 웹훅에만 적용) 서명 헤더를 요구·검사하지 않고 secret 없이 `Webhooks.ParseCounsel*`로 파싱(본문 크기·JSON 중첩·형식 검사는 동일), 응답 `{"code":"A000","result":"Success"}`
- 빈 값·공백뿐인 secret, 0 이하 tolerance는 `BizgoConfigurationException`

### 테스트 도구 (`Bizgo.Sdk.Comm.Testing`)
- `FakeBizgoHandler`(요청 기록, operation별 기본 성공 응답, 오류·연결 오류 주입), `WebhookSigner`
