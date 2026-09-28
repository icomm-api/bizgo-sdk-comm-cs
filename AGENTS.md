# AGENTS.md — bizgo-sdk-comm-cs

AI 코딩 도구와 기여자가 이 저장소를 수정할 때 따르는 규칙입니다. SDK를 *사용하는* 코드를 쓸 때는 [llms.txt](llms.txt)를 보세요.
설계 규약은 [bizgo-api-spec/docs/SDK-DESIGN.md](https://github.com/icomm-api/bizgo-api-spec/blob/main/docs/SDK-DESIGN.md)이고, 참조 구현은 Python SDK입니다.

## 명령

```bash
dotnet build -c Release                              # 경고도 오류(TreatWarningsAsErrors, 분석기 latest-recommended)
dotnet test -c Release                               # 전부 가짜 HttpMessageHandler. 네트워크·API Key 불필요
dotnet format --verify-no-changes
dotnet run --project tools/Bizgo.Codegen             # 모델·리소스·operation 표·웹훅 파서 재생성
dotnet run --project tools/Bizgo.Codegen -- --check  # CI: 생성 결과가 커밋과 같은지 (테스트 GeneratedCodeTests도 검사)
dotnet list package --vulnerable --include-transitive
dotnet pack src/Bizgo.Sdk.Comm -c Release -o artifacts
dotnet pack src/Bizgo.Sdk.Comm.Testing -c Release -o artifacts
```

변경 후 위 명령이 모두 통과해야 합니다.

릴리스 버전(현재 1.2.0)은 `src/Bizgo.Sdk.Comm/Bizgo.Sdk.Comm.csproj`와 `src/Bizgo.Sdk.Comm.Testing/Bizgo.Sdk.Comm.Testing.csproj`의 `<Version>`에서 같게 관리합니다(비즈고 SDK 6종 공통 버전, `User-Agent`/`X-Bizgo-Client`의 SDK 버전은 어셈블리 버전에서 읽음). 릴리스 태그는 `v<버전>`이며 CHANGELOG에 같은 버전 절을 둡니다.

## 구조

```
spec/openapi.yaml, spec/error-codes.json   # bizgo-api-spec에서 복사한 스펙 (직접 수정 금지, 스펙 저장소에서 고친 뒤 복사)
tools/Bizgo.Codegen/                       # 스펙 → C# 생성기 (YamlDotNet은 이 도구에서만 사용)
  Generator.cs                             #   모델(Request/Response/Shared/Form), JSON 컨텍스트, 서비스 에러코드 표
  GeneratorRequiredIf.cs                   #   x-sdk-required-if → 모델 ValidateInto의 조건부 필수 검사
  GeneratorApi.cs                          #   x-sdk-* → 리소스 클래스·메서드, BizgoOperation 표, BizgoClient 속성, 웹훅 파서
src/Bizgo.Sdk.Comm/
  BizgoClient.cs, BizgoClientOptions.cs    # 클라이언트, 환경, 옵션(RateLimit, Hooks, AppInfo)
  BizgoOperation.cs, RateLimit.cs, Observability.cs  # operation 메타데이터, 토큰 버킷, hooks·ActivitySource
  Exceptions.cs                            # 예외 계층 (SDK-DESIGN §5)
  SendTypes.cs, DateTypes.cs, FileUpload.cs, Webhooks.cs
  Resources/                               # 직접 작성한 P0: Send(+ SendResource.Bulk.cs), Files, Reports, Messages (partial)
  Models/ModelExtensions.cs                # IChannelMessage, SendResult, ReportBatch, MessagePage, MoPage, 생성 모델 partial 확장
  Internal/                                # Transport(헤더·속도 제한·재시도·봉투 해석·hooks), ErrorMapper, RequestValidator, EucKr, ClientConfig,
                                           # Http, Runtime(생성 코드 헬퍼: Results, Paging, MultipartForm, ModelText/PhoneMask), Identification, ExceptionJson
  Generated/                               # 생성됨 (직접 수정 금지): Models/*.g.cs, Resources/*.g.cs, BizgoOperation.g.cs, BizgoClient.g.cs,
                                           # Webhooks.g.cs, BizgoJsonContext.g.cs, ServiceCodes.g.cs
src/Bizgo.Sdk.Comm.Testing/                # 별도 패키지: FakeBizgoHandler(요청 기록·기본 응답·오류 주입), WebhookSigner
tests/Bizgo.Sdk.Comm.Tests/                # xUnit. net8.0, net10.0, (Windows) net48 = netstandard2.0 빌드 검증
                                           # OperationCoverageTests: 스펙의 모든 operation을 실제로 호출해 메서드·경로·재시도·페이지를 검사
examples/Bizgo.Examples/                   # 실행 가능한 예제. ExamplesTests가 가짜 서버로 실행
```

## 규칙

1. **생성 파일은 손으로 고치지 않습니다.** 모델을 바꾸려면 bizgo-api-spec을 고치고 `spec/`에 복사한 뒤 재생성합니다. 동작을 더하려면 `partial` 클래스를 `Models/`에 둡니다.
2. 새 엔드포인트는 **직접 작성하지 않습니다.** bizgo-api-spec에 operation과 `x-sdk-resource`/`x-sdk-method`/`x-sdk-retry`(+ `x-sdk-pagination`, `x-sdk-result`, `x-sdk-rate`)를 넣고 `spec/`에 복사한 뒤 재생성합니다. `OperationCoverageTests`가 스펙의 모든 operation을 실제로 호출하므로 따로 목록을 갱신할 필요는 없습니다. 편의 메서드를 직접 만들면 `GeneratorApi.cs`의 `HandWritten` 표에 operationId를 넣습니다(같은 이름이면 직접 작성한 메서드가 우선).
3. 공개 API에는 XML 문서 주석을 답니다(빌드가 강제). 반환 타입은 생성 모델이나 결과 타입입니다(`JsonElement`/`Dictionary` 반환 금지).
4. 재시도 정책: 생성 메서드는 스펙의 `x-sdk-retry`. 직접 작성한 것은 조회·DELETE `RetryPolicy.Safe`, 발송은 `IdempotencyKey`가 있을 때만 `Safe`, 업로드는 `RateLimitOnly`. 3xx는 재시도하지 않습니다. 속도 제한 send 버킷은 스펙의 `x-sdk-rate: send`만 씁니다(목록을 코드에 적지 않음).
   멱등성 TTL: `idempotencyKey`가 있고 `idempotencyTtl`이 null이면 `BizgoClient.DefaultIdempotencyTtl`(86400)을 보냅니다(없으면 A309, sandbox 확인). 명시한 값(0 포함)은 그대로, 키가 없으면 더하지 않습니다. 규칙은 `JsonBody.Create(value, typeInfo, key, ttl)`/`Idempotency.EffectiveTtl`(Internal/Http.cs) 한 곳에만 두고, 직접 작성한 발송·`BulkAsync`는 `RequestAsync`로, 생성 operation은 본문 스키마에 두 필드가 모두 있으면 생성기가 이 오버로드를 씁니다. 사용자 객체는 바꾸지 않고 직렬화된 본문에만 채웁니다. 테스트: `IdempotencyTtlTests`, `GeneratedCodeTests`.
   수신자별 `A301`(SDK-DESIGN §12 규칙 4): 같은 키 재발송은 HTTP 200에 `destinations[].code = A301`입니다. `SendResult.Duplicates`/`BulkSendResult.Duplicates`로 돌려주고 `Failed`·`Succeeded`에 넣지 않습니다(모두 A301인 청크도 오류 아님). 요청 단계 `A301`은 `BizgoDuplicateRequestException` 그대로. 테스트: `SendTests`, `ConvenienceTests`, `TestingPackageTests`.
5. 라이브러리 코드의 `await`에는 `ConfigureAwait(false)`를 붙입니다(CA2007). netstandard2.0에 없는 API는 `#if`로 나눕니다.
6. 예제·README의 코드는 실제로 동작해야 합니다. 예제를 바꾸면 `ExamplesTests`도 맞춥니다.
7. 문서에 없는 동작을 가정하지 않습니다. 불확실하면 스펙에 `x-unverified`로 표시하고 bizgo-api-spec의 GitHub Issues에 알립니다.
8. SDK-DESIGN §12의 강화 규칙(19개)마다 `HardeningTests`·`ConvenienceTests`·`CounselWebhookTests`에 재현 테스트가 있습니다. 동작을 바꾸면 테스트도 함께 바꾸고 규칙 번호를 주석에 남깁니다.
9. 조건부 필수(`x-sdk-required-if`, 스펙 AGENTS.md 11번·SDK-DESIGN §4)는 생성기(`GeneratorRequiredIf.cs`)가 스키마의 규칙을 읽어 그 모델의 `ValidateInto` 끝에 타입 있는 검사로 만듭니다. 규칙 목록을 코드에 적지 않습니다. `$.` 경로는 요청 본문 루트 기준입니다: 본문 스키마의 `ValidateInto`가 `RequestValidator.Root`를 설정하고, 규칙은 첫 속성을 가진 본문 스키마마다 `v.Root is <본문>` 분기로 검사합니다(채널 모델만 `Validate()`하면 건너뜀). 이유 문자열은 조건(필드 경로·연산자·스펙 값)만 담고 입력값을 담지 않습니다. 형식이 스펙 규칙과 다르면(연산자 2개, 없는 속성, `[]` 불일치) 생성기가 실패합니다. 테스트: `RequiredIfTests`.
10. 사용자 콜백(`Log`, hooks)의 예외는 삼키고 결과에 영향을 주지 않습니다. 성공 응답에서 null을 돌려주지 않습니다(빈 객체/목록).

## 보안 규칙 (오픈소스 저장소)

- **로그·예외에 민감정보 금지**: API Key, 요청 본문, 쿼리 문자열, 전화번호, 웹훅 secret을 `Log` 콜백, 예외 메시지, `ToString()`에 넣지 않습니다. `HttpRequestException`/`JsonException` 등은 `InnerException`으로 붙이지 않습니다. 관련 테스트: `ErrorsAndSecurityTests`.
- 검증 오류(`BizgoValidationException`)에는 필드 경로와 이유만 넣습니다. 입력값을 넣지 않습니다.
- TLS 검증을 끄는 옵션, `http://` base URL(localhost 제외), 리다이렉트를 따르는 코드를 추가하지 않습니다. 사용자 URL로 Authorization 헤더를 보내지 않습니다.
- 사용자 `HttpClient`를 받는 옵션을 다시 만들지 않습니다(SDK가 인증·리다이렉트·쿠키를 검사할 수 없음). `HttpMessageHandler`만 받고 `Transport.CheckHandler`로 검사합니다. async 콜백은 `ClientConfig.CheckCallback`이 거부합니다.
- 강력한 이름 서명은 하지 않습니다(README 보안 절). 서명이 필요해지면 public signing용 공개 키만 커밋하고 비밀 키는 저장소에 두지 않습니다.
- 경로 파라미터는 `PathSegment.Escape()`로 인코딩합니다(`.`/`..` 거부).
- 설정은 인스턴스별입니다. static 가변 상태, 전역 `Encoding.RegisterProvider` 같은 프로세스 전역 변경을 하지 않습니다.
- 테스트·예제 값은 placeholder만 씁니다: 전화번호 `01000000000`, `01000001234`, 키 `test-api-key-not-real`, `SENDER_KEY_EXAMPLE`. 실제 키·번호·발신프로필 키를 커밋하지 않습니다(gitleaks가 pre-commit·CI에서 검사).
- 런타임 의존성: net8.0은 없음, netstandard2.0은 `System.Text.Json`, `System.Text.Encoding.CodePages`, `Microsoft.Bcl.AsyncInterfaces`만. `ActivitySource`(OpenTelemetry)는 net8.0에서만 쓰고 netstandard2.0에는 `System.Diagnostics.DiagnosticSource`를 추가하지 않습니다(`#if NET8_0_OR_GREATER`). 추가가 필요하면 이유를 PR에 적습니다. 버전은 `Directory.Packages.props`에서만 관리하고 lock 파일을 커밋합니다.
- hooks·span·`BizgoOperation`에는 경로 **템플릿**만 넣습니다. 실제 경로 값, 쿼리, 본문, 헤더 값은 넣지 않습니다. 모델·옵션 `ToString()`은 전화번호 필드(`to`, `from`, `phoneNumber` 등)를 가리고 `text`/`content`류는 길이만 씁니다(`Generator.PhoneFields`/`ContentFields`).
- 리포트·MO 웹훅 서명은 `X-IB-Signature` = `HmacSHA256(secret, X-IB-Timestamp)`입니다(비즈고 확인). 다른 서명 헤더 이름이나 `sha256=` 같은 접두어는 받지 않고, 출력 인코딩은 아직 확인되지 않아 hex(대소문자 무시)·base64를 모두 허용합니다. 웹훅 secret은 비즈고에 요청해 받습니다.
- 상담톡 웹훅에는 서명이 없습니다(서명은 리포트·MO 웹훅에만 적용). 상담톡 파서는 서명을 요구·검사하지 않고 secret 없이 쓸 수 있습니다(`Webhooks.ParseCounsel*`). 생성기는 스펙에 `X-IB-Signature` 헤더 파라미터가 있는 웹훅만 검증 파서로 만듭니다. 리포트·MO 웹훅은 항상 서명이 필요하며, 이 검증을 끄는 옵션을 추가하지 않습니다.
- GitHub Actions는 검증된 커밋 SHA로 고정하고 버전 주석을 답니다. 새 action이 필요하면 SHA를 추측하지 말고 관리자에게 받습니다. gitleaks 이미지는 digest로 고정합니다.
