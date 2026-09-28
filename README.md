# bizgo-sdk-comm (C# / .NET)

[비즈고(Bizgo)](https://bizgo.io) 커뮤니케이션 API의 .NET SDK입니다.
SMS/LMS/MMS, 국제문자, RCS, 카카오 알림톡·브랜드메시지, 네이버 톡톡을 하나의 클라이언트로 발송하고, 리포트·이력·통계를 조회하고, 웹훅을 검증합니다.
예약 발송, 발신프로필·템플릿 관리, 인사이트, 상담톡 등 **커뮤니케이션 API 전체(operation 146개)** 를 스펙에서 생성한 메서드로 제공하고, 대량 발송·클라이언트 속도 제한·테스트 도구·관측(hooks, OpenTelemetry)을 함께 제공합니다.

- NuGet `Bizgo.Sdk.Comm`(+ 테스트용 `Bizgo.Sdk.Comm.Testing`) · `netstandard2.0`(.NET Framework 4.6.1+ 포함)과 `net8.0` · 모든 I/O는 `async`/`CancellationToken`
- net8.0에서는 런타임 의존성이 없습니다(BCL만). netstandard2.0에서는 `System.Text.Json`, `System.Text.Encoding.CodePages`, `Microsoft.Bcl.AsyncInterfaces`만 씁니다.
- 요청 모델은 보내기 전에 검증합니다: 필수 필드, 길이, **바이트 길이**(SMS 90byte 등, EUC-KR/CP949 기준), EUC-KR 밖 문자(이모지), 수신자 200명, 허용 값, 발송 방식·타입별 조건부 필수(스펙의 `x-sdk-required-if`)
- 스펙: [bizgo-api-spec](https://github.com/icomm-api/bizgo-api-spec) (OpenAPI 3.1) · 원문: [API 레퍼런스](https://developers.bizgo.io/api-sdk/api-reference)

> 이전 `Infobank.Omni.Sdk.Cs`(OMNI API, ID/PW 인증)를 쓰고 있다면 [이전 SDK에서 옮기기](#이전-sdk에서-옮기기)를 참고하세요.

## 설치

```bash
dotnet add package Bizgo.Sdk.Comm --version 1.2.0
```

## 시작하기

1. 콘솔 `발송관리 > 연동관리`에서 **API Key**를 발급하고, 호출할 서버의 **공인 IP를 등록**합니다.
2. 키를 환경변수로 설정합니다. 키는 코드나 저장소에 쓰지 않습니다.

```bash
export BIZGO_API_KEY=...
```

3. sandbox(실제 발송 없음)에서 먼저 확인합니다.

```csharp
using Bizgo;

using var client = new BizgoClient(new BizgoClientOptions { Environment = BizgoEnvironment.Sandbox }); // 키는 BIZGO_API_KEY에서 읽음

var result = await client.Send.SmsAsync("01000000000", from: "01000000000", text: "[비즈고] 인증번호는 123456 입니다.");

Console.WriteLine(string.Join(",", result.MsgKeys)); // 접수된 메시지 키
Console.WriteLine(result.Failed.Count);              // 접수 단계에서 거절된 수신자 수 (A301 중복은 result.Duplicates)
```

운영에 보낼 때는 `Environment`를 생략하거나 `BizgoEnvironment.Production`을 씁니다.
`BizgoClient`는 스레드 안전합니다. 키마다 하나 만들어 재사용하고(DI에서는 singleton), 다 쓰면 `Dispose`합니다.

> **접수 ≠ 발송 완료.** `Send.*`의 결과는 접수 결과입니다. 최종 결과는 [리포트](#리포트)로 받습니다.

## 발송

모든 채널은 `client.Send.OmniAsync()` 하나로 보냅니다. `messages`에 여러 개를 넣으면 **앞 메시지가 실패할 때 다음 메시지로 대체발송**됩니다.

```csharp
using Bizgo.Models;

var result = await client.Send.OmniAsync(
    new Destination("01000000000") { ReplaceWords = new Dictionary<string, string> { ["name"] = "홍길동" } }, // 최대 200명
    new IChannelMessage[]
    {
        new AlimtalkMessage { SenderKey = "...", TemplateCode = "...", MsgType = "AT", Text = "#{name}님, 주문이 접수되었습니다." },
        new SmsMessage { From = "01000000000", Text = "#{name}님, 주문이 접수되었습니다." }, // 알림톡 실패 시
    },
    new SendOptions
    {
        IdempotencyKey = "order-20260923-0001", // 권장: 재시도해도 중복 발송되지 않음
        Ref = "order-20260923-0001",            // 리포트에 그대로 돌아오는 참조값
    });
```

`IdempotencyKey`를 보낼 때 비즈고는 `idempotencyTtl`(0~86400초)도 함께 요구합니다(없으면 `A309`). `IdempotencyTtl`을 비워 두면 SDK가 `BizgoClient.DefaultIdempotencyTtl`(86400초 = 24시간)을 채워 보냅니다. 직접 지정한 값(0 포함)은 그대로 보내고, 키가 없으면 TTL을 더하지 않습니다. `Send.RequestAsync(request)`, `BulkAsync` 청크, 본문에 두 필드가 모두 있는 생성 operation에도 같은 규칙이 적용되며, 넘긴 요청 객체는 바꾸지 않습니다(직렬화된 본문에만 채움).

`to`에는 번호 하나(`string`), 번호 배열·`List<string>`, `Destination`(배열·목록)을 넣을 수 있습니다. 다른 시퀀스는 `Recipients.Of(...)`로 감쌉니다.

| 채널 | 모델 | 간편 메서드 |
|---|---|---|
| SMS (90byte) | `SmsMessage` | `Send.SmsAsync()` |
| LMS (2,000byte) | `MmsMessage` (`FileKey` 없음) | `Send.LmsAsync()` |
| MMS | `MmsMessage` (`FileKey` 최대 3개) | `Send.MmsAsync()` |
| 국제문자 | `InternationalMessage` | |
| RCS | `RcsMessage` | |
| 카카오 알림톡 | `AlimtalkMessage` | |
| 카카오 브랜드메시지 | `BrandMessage` | |
| 네이버 톡톡 | `NaverTalkMessage` | |

- 모델은 스펙에서 생성되며, **설정한 속성만** 직렬화합니다(스펙 기본값을 임의로 보내지 않음). JSON 필드명은 API 이름(`from`, `senderKey`)입니다.
- 보내기 전 검증에 실패하면 `BizgoValidationException`이 납니다. `Errors`에 필드 경로(`messageFlow[0].sms.text`)와 이유가 있고, 입력값(전화번호 등)은 들어가지 않습니다. 직접 확인하려면 `message.Validate()`를 부릅니다.
- 알림톡 **전문 발송**(`SendType`을 비움)에는 `MsgType`(`AT` 텍스트형 / `AI` 이미지형)과 `Text`가 필수입니다. 빠지면 비즈고가 `A523`으로 거절하므로 SDK가 보내기 전에 막습니다. 템플릿의 타입은 `client.Alimtalk.Templates.GetAsync(...)` 결과의 `MsgType`을 그대로 쓰면 됩니다. **템플릿 자동 치환 발송**(`SendType = "template"`)에서는 `MsgType`·`Text`가 필요 없고 모든 `destinations[].ReplaceWords`가 필수입니다.
- 이런 조건부 필수 규칙(알림톡·브랜드메시지 `sendType`별 필드, 브랜드메시지 버튼 `WL`의 `UrlPc`·`UrlMobile`, RCS `Header = "1"`의 `Footer`, 상담톡 메시지 타입별 첨부 등)은 스펙의 `x-sdk-required-if`에서 생성됩니다. 오류 이유에는 조건만 들어갑니다(`messageFlow[0].alimtalk.msgType`: `sendType != template일 때 필수입니다`). `destinations[]`처럼 요청 본문 기준인 규칙은 요청 전체를 검증할 때(발송 시, `SendOmniRequest.Validate()`) 검사하고, 채널 모델만 `Validate()`할 때는 건너뜁니다.
- 큐 등에서 받은 JSON은 `SendOmniRequest.FromJson(json)`으로 읽습니다. 모델에 없는 필드(오타)는 거부합니다. 보낼 때는 `client.Send.RequestAsync(request)`.

### 이미지

```csharp
var uploaded = await client.Files.UploadMmsAsync(FileUpload.FromPath("banner.jpg")); // jpg, 최대 300KB (넘으면 보내기 전 오류)
await client.Send.MmsAsync("01000000000", "01000000000", "...", new[] { uploaded.FileKey! });

await client.Files.UploadRcsAsync(FileUpload.FromBytes(bytes, "card.png"));                                    // → .Media
await client.Files.UploadBrandMessageAsync(FileUpload.FromStream(stream, "wide.jpg"), BrandImageKind.Wide);     // → .ImgUrl
```

파일은 한 번 읽어 버퍼에 두므로, 재시도해도 같은 내용이 전송됩니다.

## 리포트

콘솔에서 API Key별로 리포트 수신 방식(POLLING 또는 WEBHOOK)을 정합니다.

**Polling**: 처리에 성공한 배치만 수신 확인(ack)합니다. 처리 함수가 예외를 내면 같은 배치를 다시 받습니다.

```csharp
await client.Reports.ConsumeAsync(async (reports, ct) =>
{
    foreach (var r in reports)
    {
        await db.UpsertAsync(r.MsgKey, r.ReportCode, ct); // 같은 리포트가 다시 올 수 있으니 upsert
    }
});
```

**Webhook**: 서명을 검증하고 5초 안에 `{"msgKey": ...}`로 응답합니다. 웹훅 secret은 비즈고에 요청해 별도로 받습니다. ASP.NET Core 예:

```csharp
var receiver = new WebhookReceiver(Environment.GetEnvironmentVariable("BIZGO_WEBHOOK_SECRET")!);

app.MapPost("/bizgo/report", async (HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    try
    {
        var report = receiver.Report(name => request.Headers[name], buffer.ToArray()); // 헤더 이름 대소문자 무시
        queue.Enqueue(report);                                                          // 무거운 처리는 비동기로, MsgKey로 중복 제거
        return Results.Text(Webhooks.AckJson(report.MsgKey!), "application/json");
    }
    catch (BizgoWebhookVerificationException)
    {
        return Results.StatusCode(401);
    }
});
```

> 웹훅 서명(`HmacSHA256(secret, X-IB-Timestamp)`)과 timestamp 허용 오차를 검증하세요. 운영 환경에서는 HTTPS, 비즈고 웹훅 발신 IP 허용 목록, `msgKey` 기준 중복 제거를 함께 적용하고, 중요한 판단은 리포트·상태 조회 API로 결과를 확인하세요. 서명 출력 인코딩은 아직 확인되지 않아 hex(대소문자 무시)·base64를 모두 받습니다.

**상담톡 웹훅**(`CounselMessage`, `CounselReference`, `CounselExpiredSession`, `CounselSeenInfo`, `CounselPersonalInfo`, `CounselCertResult`, `CounselResult`): 상담톡 웹훅에는 서명이 없습니다(서명은 리포트·MO 웹훅에만 적용). 그래서 webhook secret 없이 정적 메서드 `Webhooks.ParseCounsel*`로 본문을 파싱합니다. 응답은 `Webhooks.CounselAckJson()`(`{"code":"A000","result":"Success"}`)입니다.

```csharp
// secret 불필요: 상담톡 웹훅만 받는 서버라면 WebhookReceiver를 만들지 않아도 됩니다
app.MapPost("/bizgo/counsel", async (HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    try
    {
        var message = Webhooks.ParseCounselMessage(buffer.ToArray()); // 본문 1MB·JSON 중첩 64단계·형식 검사
        queue.Enqueue(message);                                        // 개인정보가 들어 있으니 로그에 남기지 마세요
        return Results.Text(Webhooks.CounselAckJson(), "application/json");
    }
    catch (BizgoWebhookVerificationException)
    {
        return Results.StatusCode(400);
    }
});
```

> 리포트·MO용 `WebhookReceiver`가 이미 있다면 `receiver.CounselMessage(headers, body)` 등도 같은 동작입니다(서명 헤더를 요구·검사하지 않고, 와도 무시). 상담톡 엔드포인트도 HTTPS, 비즈고 웹훅 발신 IP 허용 목록, `msgKey`가 있는 이벤트는 `msgKey` 기준 중복 제거를 적용하세요. 리포트·MO 웹훅은 항상 서명이 필요합니다.

**개별 조회**: `client.Reports.InquiryAsync(msgKey)` (30일 이내)

## 조회

```csharp
await client.Messages.StatusAsync(msgKey);                 // 단건 상태 (대체발송 시 채널별 항목)
await client.Messages.StatusByRequestIdAsync(requestId);   // 동보 요청 전체 (msgKey에서 끝 3자리를 뺀 값)
await client.Messages.StatisticsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 23), serviceType: "SMS");

await foreach (var m in client.Messages.EnumerateHistoryAsync(DateTimeOffset.UtcNow.AddHours(-1), new[] { "SMS", "ALIMTALK" }))
{
    // 페이지(lastSeq)를 자동으로 따라감
}

await foreach (var mo in client.Messages.EnumerateMoHistoryAsync(new DateTime(2026, 9, 23, 9, 0, 0)))
{
    // MO(수신) 이력
}
```

- 날짜·시각: `DateTimeOffset`과 `DateTimeKind.Utc/Local`인 `DateTime`은 KST로 바꿔 보냅니다. `DateTimeKind.Unspecified`(`new DateTime(...)`)는 KST 시각으로 봅니다. 문자열은 그대로 보냅니다(통계 날짜는 `YYYYMMDD`만 허용).
- `limit`는 1~1000입니다. 조회 API의 기본 호출 한도는 초당 5회입니다([속도 제한](#속도-제한-클라이언트)).

## 전체 API (스펙에서 생성한 리소스)

스펙의 operation 146개가 모두 `client.<리소스>.<메서드>Async(...)`로 있습니다. P0 메서드(`Send`, `Files`, `Reports`, `Messages`)는 직접 작성했고, 나머지는 `spec/openapi.yaml`의 `x-sdk-*` 메타데이터로 **생성**합니다(`src/Bizgo.Sdk.Comm/Generated/Resources`).

| 리소스 | 예 |
|---|---|
| `Reservations`, `Reservations.Recipients` | 예약 발송 생성·조회·수정·취소·중지·재개, 수신자 추가·조회·삭제 |
| `Insights.Alimtalk` / `.BrandMessage` / `.Rcs` | 반응·발송 통계 |
| `Kakao.Senders` / `.Categories` / `.Groups` / `.Sanctions` | 발신프로필, 카테고리, 그룹, 제재 조회 |
| `Alimtalk.Templates` / `.TemplateCategories` / `.PublicTemplates` | 알림톡 템플릿 등록·검수·조회 |
| `BrandMessage.Templates` / `.GroupSends` / `.Audience` / `.FriendGroups` / `.Videos` / … | 브랜드메시지 템플릿, 동보, 대상 확인, 친구 그룹 |
| `Rcs.Brands` / `.Chatbots` / `.Templates` / `.TemplateForms` / … | RCS 브랜드, 대화방, 템플릿 |
| `Counsel.Messages` / `.Sessions` / `.Users` / `.SystemMessages` / … | 카카오 상담톡 |
| `Files` | 알림톡 템플릿 이미지, 브랜드메시지 카탈로그 이미지 등 추가 업로드 |

규칙:

- 인자 순서: 경로 파라미터(필수, 한 세그먼트로 인코딩) → 요청 본문 모델 → 필수 헤더·쿼리 파라미터 → 선택 쿼리 파라미터를 담는 `...Options` 객체 → `CancellationToken`.
- 요청 본문 모델은 보내기 전에 검증합니다(필수, 조건부 필수 `x-sdk-required-if`, 길이, 허용 값, 패턴, 항목 수, 모델에 없는 필드). 오류 메시지에 값은 들어가지 않습니다.
- 반환값은 스펙의 `x-sdk-result`(기본 `data.data`) 부분입니다. 성공 응답에 값이 없어도 **null을 돌려주지 않습니다**(빈 객체 / 빈 목록). 데이터가 없는 operation은 `Task`입니다.
- 재시도는 `x-sdk-retry`를 따릅니다: 조회·삭제(`safe`)는 429·5xx·네트워크 오류, 생성·발송·업로드(`rate_limit_only`)는 429만.
- 목록 operation에는 모든 페이지를 읽는 `Enumerate...Async`(`IAsyncEnumerable<T>`)가 함께 있습니다(`ListAsync` → `EnumerateAsync`).
- multipart operation(템플릿 이미지, RCS 브랜드 수정 등)은 `FileUpload` 속성을 가진 요청 모델을 받습니다. 파일은 한 번만 읽어 재시도에 재사용하고, 모델 속성은 JSON 파트로 보냅니다.

```csharp
using Bizgo.Models;

// 알림톡 템플릿: 한 페이지, 전체 순회, 단건 조회
IReadOnlyList<AlimtalkTemplate> page = await client.Alimtalk.Templates.ListAsync("SENDER_KEY_EXAMPLE", new AlimtalkTemplatesListOptions { InspectionStatus = "APR", Limit = 100 });
await foreach (var template in client.Alimtalk.Templates.EnumerateAsync("SENDER_KEY_EXAMPLE"))
{
    Console.WriteLine(template.TemplateCode);
}

AlimtalkTemplate detail = await client.Alimtalk.Templates.GetAsync("SENDER_KEY_EXAMPLE", "TEMPLATE_CODE_EXAMPLE");

// 예약 발송: 결과에 resvKey와 수신자별 접수 결과가 함께 옵니다
ReservationCreateServiceResult reservation = await client.Reservations.CreateAsync(new ReservationCreateRequest
{
    Destinations = new[] { new Destination("01000000000") },
    MessageFlow = new[] { ReservationMessageFlowItem.Of(new SmsMessage { From = "01000000000", Text = "예약 안내" }) },
    ResvSendTime = "2026-10-01T09:00:00",
});
Console.WriteLine(reservation.ResvKey);

// RCS 브랜드 수정(multipart): 파일 파트 + JSON 파트
await client.Rcs.Brands.UpdateAsync(new RcsBrandUpdateRequest
{
    BrandId = "BRAND_ID_EXAMPLE",
    RegBrand = new RcsBrand { BrandId = "BRAND_ID_EXAMPLE", Name = "예시 브랜드" },
    BrandProfile = FileUpload.FromPath("profile.png"),
});
```

모든 operation 목록과 메타데이터(`operationId`, `resource.method`, HTTP 메서드, 경로 템플릿, 재시도, 속도 제한 버킷)는 `BizgoOperation.All`에 있습니다.

## 대량 발송

`Send.BulkAsync`는 수신자 수에 제한이 없습니다. 200명(`ChunkSize`, 1~200)씩 나눠 `OmniAsync`를 부르고, 동시에 `Concurrency`(기본 4)개까지 보내며, 발송 속도 제한(수신자 수 기준)을 함께 적용합니다.

```csharp
var result = await client.Send.BulkAsync(
    recipients,                                                     // string[] / List<string> / Destination[] …
    new IChannelMessage[] { new SmsMessage { From = "01000000000", Text = "[비즈고] 점검 안내" } },
    new BulkSendOptions { IdempotencyKeyPrefix = "notice-20261001", Ref = "notice" });

Console.WriteLine(result.Succeeded.Count + " 접수, " + result.Failed.Count + " 거부");
foreach (var error in result.Errors)
{
    // 청크 번호와 수신자 인덱스 범위만 있습니다(번호는 없음)
    Console.WriteLine(error.ChunkIndex + ": " + error.Start + "번부터 " + error.Count + "명, " + error.Error.GetType().Name);
}
```

- 보내기 전에 모든 청크를 검증합니다. 하나라도 잘못되면 아무것도 보내지 않습니다(오류 경로의 인덱스는 전체 목록 기준).
- 한 청크가 어떤 이유로 실패해도(API 오류, 네트워크, 읽을 수 없는 응답, 취소) 나머지 청크는 계속 보내고, 이미 접수된 결과는 `Results`에 남습니다.
- `IdempotencyKeyPrefix`를 주면 청크마다 `<prefix>-<chunkSize>-<시작 인덱스>-<hash8>`(청크 번호 목록의 SHA-256 앞 8자리)를 멱등성 키로 씁니다. **재실행은 같은 목록·같은 `ChunkSize`로** 하세요. 이미 접수된 청크는 다시 발송되지 않고, 그 수신자는 `result.Duplicates`로 돌아옵니다(수신자가 모두 `A301`인 청크도 오류가 아님). 실패한 청크만 다시 보내려면 `Errors`의 청크 번호를 쓰세요. 키는 200자 이하여야 합니다. `IdempotencyTtl`을 비워 두면 청크마다 86400(`BizgoClient.DefaultIdempotencyTtl`)을 보냅니다.
- 연결 오류·`BizgoInvalidResponseException`으로 실패한 청크는 접수됐을 수도 있으니 멱등성 키 없이 다시 보내기 전에 상태 조회로 확인하세요.

## 속도 제한 (클라이언트)

비즈고 기본 한도는 **발송 API 초당 200건(메시지 = 수신번호 기준)**, 그 외 API 초당 5회입니다. 클라이언트마다 토큰 버킷 2개로 요청 전에 기다립니다(기본 켜짐, 스레드를 막지 않는 `await` 대기).

| 버킷 | 대상 | 비용 | 기본 |
|---|---|---|---|
| send | 스펙에서 `x-sdk-rate: send`인 operation: `sendOmni`(`Send.*`, `BulkAsync` 포함), `createReservation`, `addReservationRecipients`, `createBrandMessageGroupSend`, `sendCounselPlain`, `sendCounselRich` | 요청의 `destinations` 수(최소 1, 상담톡·친구 그룹 동보는 1) | 초당 200 |
| other | 그 밖의 모든 operation | 요청마다 1 | 초당 5 |

```csharp
new BizgoClientOptions { RateLimit = new BizgoRateLimit { SendPerSecond = 100, OtherPerSecond = 2 } }; // 한도 변경
new BizgoClientOptions { RateLimit = null };                                                        // 끄기
```

- 버킷은 가득 찬 상태(용량 = 초당 한도)로 시작합니다. 재시도를 포함해 시도마다 기다립니다. 요청 비용이 용량보다 크면 버킷이 가득 찰 때까지 기다렸다 보내고 부족분은 다음 요청이 갚습니다.
- **이 제한은 한 프로세스 안의 한 클라이언트에만 적용됩니다.** 한도는 계정 단위이므로 여러 프로세스·서버가 같은 키를 쓰면 따로 조율해야 합니다. 서버의 429는 계속 자동 재시도합니다.

## 테스트 (Bizgo.Sdk.Comm.Testing)

내 코드를 네트워크·API Key 없이 테스트하려면 테스트 프로젝트에만 별도 패키지를 추가합니다(운영 코드에서는 참조할 필요가 없습니다).

```bash
dotnet add package Bizgo.Sdk.Comm.Testing --version 1.2.0
```

```csharp
using Bizgo.Testing;

using var fake = new FakeBizgoHandler();
using var client = fake.CreateClient(); // placeholder 키, sandbox, 속도 제한·재시도 끔

var result = await client.Send.SmsAsync("01000000000", "01000000000", "hello"); // 기본: 수신자마다 A000 + 가짜 msgKey
Console.WriteLine(fake.LastRequest!.OperationId);                              // "sendOmni"
Console.WriteLine(fake.LastRequest.Json!["messageFlow"]![0]!["sms"]!["text"]);    // 보낸 JSON 본문

fake.On("sendOmni").RateLimited();                                  // 다음 호출: BizgoRateLimitException
fake.On("sendOmni").Fail(BizgoErrorLayer.Gateway, 401, "A401");     // 그다음: BizgoAuthenticationException
fake.On("sendOmni").SendResult("A000", "A306");                     // 그다음: 두 번째 수신자 거부
fake.On("sendOmni").SendDuplicates();                               // 같은 멱등성 키 재발송: 모든 수신자 A301 -> result.Duplicates
fake.On("GET", "/api/comm/v1/report/inquiry/{msgKey}").RespondData("{\"report\":[]}");

var signed = WebhookSigner.Sign("test-webhook-secret-not-real", "{\"msgKey\":\"KEY001\"}"); // 웹훅 핸들러 테스트용 헤더 + 본문
```

- 기본 응답: 발송(`sendOmni`, `createReservation`, `addReservationRecipients`)은 수신자마다 `A000`과 가짜 `msgKey`(`createReservation`은 가짜 `resvKey`도), MMS/RCS/브랜드메시지 업로드는 가짜 `fileKey`/`media`/`imgUrl`, 그 밖에는 `data.data = {}`, 모르는 경로는 404(`A404`).
- 기록된 요청(`Requests`)에는 operation, 메서드, 경로, 쿼리, JSON 본문(multipart는 필드 이름·파일 크기)이 있고, 헤더는 **이름만** 기록합니다(API Key 값은 남지 않음).

## 관측 (hooks, OpenTelemetry)

```csharp
new BizgoClientOptions
{
    Hooks = new BizgoHooks
    {
        OnRequest = e => { /* e.OperationId, e.Operation.Name ("alimtalk.templates.list"), e.HttpMethod, e.PathTemplate, e.Attempt */ },
        OnResponse = e => { /* + e.StatusCode, e.Code, e.Layer, e.ErrorType, e.Duration, e.WillRetry */ },
    },
};
```

- hooks에는 **경로 템플릿**(`/api/comm/v1/report/inquiry/{msgKey}`)만 넘어가고 본문·쿼리·헤더 값·실제 경로 값·전화번호·키는 넘어가지 않습니다. 시도마다 호출되고, 응답 파싱 실패는 성공으로 보고하지 않습니다. hook이나 `Log`에서 난 예외는 SDK가 삼키며 결과에 영향을 주지 않습니다. 콜백은 요청 경로에서 동기로 실행되므로 짧게 두세요. `async` 람다·메서드는 클라이언트를 만들 때 `BizgoConfigurationException`으로 거부합니다(`async void`의 예외는 SDK가 잡을 수 없어 프로세스가 종료되기 때문). 비동기 작업은 큐에 넣으세요.
- OpenTelemetry: net8.0 빌드는 `System.Diagnostics.ActivitySource("Bizgo")`로 요청마다 span `bizgo <resource>.<method>`를 만듭니다(속성 `http.request.method`, `url.template`, `http.response.status_code`, `bizgo.code`, `bizgo.layer`, `bizgo.retry_count`, `bizgo.operation_id`). 추가 패키지는 필요 없고 `AddSource("Bizgo")`로 수집합니다.
- netstandard2.0 빌드(.NET Framework 등)는 `ActivitySource`에 `System.Diagnostics.DiagnosticSource` 패키지가 필요해 의존성을 늘리지 않으려고 span을 만들지 않습니다. hooks로 직접 연결하세요.

## 오류 처리

```csharp
try
{
    await client.Send.OmniAsync(...);
}
catch (BizgoValidationException e)      { /* 보내기 전 검증 실패. e.Errors (경로, 이유) */ }
catch (BizgoAuthenticationException)    { /* 키가 틀렸거나 IP가 등록되지 않음 */ }
catch (BizgoRateLimitException e)       { /* 자동 재시도 후에도 한도 초과. e.RetryAfter */ }
catch (BizgoDuplicateRequestException e) { /* 요청 단계 A301(data.code): 같은 IdempotencyKey가 이미 접수됨 (다시 발송되지 않음). e.AlreadyAccepted: SDK가 재시도한 뒤라 앞 시도가 접수된 것 */ }
catch (BizgoApiException e)             { /* 그 밖의 거절. e.Code, e.Layer, e.Description, e.TrackingId */ }
catch (BizgoConnectionException)        { /* 네트워크 오류·타임아웃. 발송은 접수됐을 수도 있음 → 상태 조회로 확인 */ }
catch (BizgoInvalidResponseException e) { /* 응답을 읽을 수 없음(형식, 16MB 초과, 중첩 64단계 초과, 3xx). e.HttpStatus, e.TrackingId, e.Body. 발송이면 접수됐을 수 있음 */ }
```

```
BizgoException
├── BizgoConfigurationException
├── BizgoValidationException
├── BizgoConnectionException ── BizgoTimeoutException
├── BizgoApiException {HttpStatus, Code, Layer, Description, TrackingId, ServerMessage, Body}
│   ├── BizgoBadRequestException (400)  ├── BizgoAuthenticationException (401)  ├── BizgoPermissionDeniedException (403)
│   ├── BizgoNotFoundException (404)    ├── BizgoDuplicateRequestException (A301)
│   ├── BizgoRateLimitException (429/A020)                                      └── BizgoInternalServerException (5xx)
├── BizgoInvalidResponseException
└── BizgoWebhookVerificationException
```

- 응답은 두 단계로 판정합니다: `common.authCode`(게이트웨이: 인증·형식) → `data.code`(상품 처리). `e.Layer`가 `Gateway`/`Service`입니다. 같은 코드라도 단계마다 뜻이 다릅니다(예: service `A301` = 멱등성 키 중복, service `A401` = paymentCode 오류).
- 메시지 형식: `HTTP 400 | service code=A306 | <result> | <설명> | infobankTrId=...`
- 발송 요청이 성공해도 **일부 수신자는 거절될 수 있습니다.** 항상 `result.Failed`를 확인하세요.
- 같은 `IdempotencyKey`로 다시 보내면 비즈고는 HTTP 200에 수신자별 `A301`로 답합니다(sandbox 2026-09-28 확인). SDK는 이 수신자를 `Failed`가 아닌 `result.Duplicates`(앞 요청에서 이미 접수됨, 새 `msgKey` 없음)로 돌려주며 `Succeeded`에도 넣지 않습니다. 그래서 같은 키로 재시도해도 안전합니다. 요청 단계 `A301`은 그대로 `BizgoDuplicateRequestException`입니다.
- 작업 큐·다른 프로세스로 오류를 넘길 때는 `e.ToJson()` / `BizgoApiException.FromJson(json)`을 씁니다(같은 예외 형식과 속성으로 복원, 형식이 다르면 `ArgumentException`). 예외는 `[Serializable]`이 아닙니다(`BinaryFormatter`는 .NET에서 폐기되고 안전하지 않음). `Body`가 포함되므로 응답 원문처럼 다루세요.
- 결과·요청 모델과 옵션 객체의 `ToString()`은 전화번호를 가리고(`010****0000`), 사람 이름·닉네임·이메일·주소(`userName`, `nickname` 등)는 첫 글자만, 본문(`text`, `message`, `content` 등 이름이 Message/Content/Text/Comment로 끝나는 필드 포함)은 길이만 보여 줍니다. 속성 값은 그대로입니다.
- snake_case JSON 필드는 PascalCase 속성이 됩니다(`phone_number` → `PhoneNumber`, `bot_event` → `BotEvent`, `created_at` → `CreatedAt`). JSON 이름은 그대로입니다.

## 재시도와 타임아웃

| 요청 | 자동 재시도 |
|---|---|
| 조회, 리포트(ack 포함) | 429, 500/502/503/504, 네트워크 오류 |
| 발송 (`IdempotencyKey` 있음) | 429, 500/502/503/504, 네트워크 오류 |
| 발송 (`IdempotencyKey` 없음), 업로드 | 429만 (중복 발송 방지) |

기본값은 최대 2회 재시도(`min(0.5·2^n, 8)초 × jitter`, `Retry-After` 우선·최대 60초, 0 이상의 유한한 값만 사용), 시도당 타임아웃 30초(연결 5초)입니다. 3xx는 재시도하지 않습니다. 생성된 operation은 스펙의 `x-sdk-retry`를 따릅니다.

모든 요청에는 `User-Agent: bizgo-sdk-comm-cs/<버전> dotnet/<버전> (<os>; <arch>)`와 `X-Bizgo-Client: bizgo-sdk-comm-cs/<버전>`가 붙습니다(비즈고의 SDK 사용 현황 집계용, 호스트명 등 자세한 값은 없음). `AppInfo = new BizgoAppInfo("myshop", "1.4.2")`를 주면 `User-Agent` 끝에 `app/myshop-1.4.2`가 붙습니다(이름 `^[A-Za-z0-9._-]{1,50}$`, 버전 `^[A-Za-z0-9._+-]{1,30}$`만 허용, 이메일·전화번호 같은 값은 넣지 마세요).

```csharp
new BizgoClientOptions
{
    MaxRetries = 3,
    Timeout = TimeSpan.FromSeconds(10),
    Log = line => logger.LogDebug("{BizgoRequest}", line), // "POST /api/comm/v1/send/omni -> 200 (85 ms, attempt 1)"만 전달
};
```

### HttpMessageHandler 직접 지정 (프록시, IHttpClientFactory)

SDK는 `HttpClient`를 **직접 만들고 소유합니다**(타임아웃은 `Timeout`/`ConnectTimeout`). 완성된 `HttpClient`는 받지 않습니다. 그 클라이언트가 어떻게 인증하고 리다이렉트를 따르는지 SDK가 확인할 수 없기 때문입니다(자체 인증이 401 뒤 발송을 다시 보내거나, 리다이렉트가 전화번호가 든 본문을 다른 호스트로 보낸 사례가 있었습니다). 프록시·클라이언트 인증서·`IHttpClientFactory`는 `HttpMessageHandler`로 넘깁니다. SDK는 넘겨받은 핸들러를 `Dispose`하지 않습니다.

```csharp
var handler = new SocketsHttpHandler
{
    Proxy = new WebProxy("http://proxy.example:3128"),
    AllowAutoRedirect = false, // 필수
    UseCookies = false,        // 필수
};
using var client = new BizgoClient(new BizgoClientOptions { HttpMessageHandler = handler });
```

```csharp
// IHttpClientFactory: 이름 붙인 클라이언트의 핸들러 체인을 넘깁니다(DelegatingHandler 체인도 검사합니다)
services.AddHttpClient("bizgo").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
services.AddSingleton(sp => new BizgoClient(new BizgoClientOptions
{
    HttpMessageHandler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("bizgo"),
}));
```

- 클라이언트를 만들 때 핸들러와 모든 `DelegatingHandler` 안쪽 핸들러를 검사합니다. `HttpClientHandler`/`SocketsHttpHandler`가 `AllowAutoRedirect = true`, `UseCookies = true`이거나 `Credentials`, `UseDefaultCredentials`, `PreAuthenticate`를 쓰면 `BizgoConfigurationException`입니다(기본값이 true인 속성이 있으니 위 예처럼 꺼 주세요). 프록시 인증(`DefaultProxyCredentials`, `Proxy.Credentials`)은 허용합니다.
- 그 밖의 핸들러 형식은 검사할 수 없습니다. 그런 핸들러가 리다이렉트를 따라가 base URL 밖의 응답을 받으면 SDK가 거부합니다. SDK가 만드는 핸들러는 리다이렉트를 따르지 않고, 3xx 응답은 원래 상태(`HTTP 307` 등)를 담은 `BizgoInvalidResponseException`입니다.
- `Authorization`, `User-Agent`, `X-Bizgo-Client`는 요청마다 SDK가 설정하며 바꿀 수 없습니다.
- **TLS**: SDK에는 TLS 검증을 끄는 옵션이 없습니다. 다만 핸들러를 넘기면 **그 핸들러의 TLS 설정**(인증서 검증 콜백, 클라이언트 인증서)이 그대로 적용되므로 검증을 끄는 콜백을 넣지 마세요.
- `ConnectTimeout`은 SDK가 만드는 핸들러(.NET 5+)에만 적용됩니다. 넘긴 핸들러에는 `Timeout`(시도당 전체 시간)만 적용됩니다.

## 보안

- API Key는 **접두어 없이** 키 값만 씁니다(`Bearer`/`ApiKey` 접두어는 401). 환경변수나 시크릿 저장소에서 읽고, 코드·저장소·클라이언트 앱에 넣지 않습니다. `ToString()`에는 키가 나오지 않습니다.
- SDK는 키, 요청 본문, 쿼리 문자열(전화번호가 들어갈 수 있음)을 로그나 예외 메시지에 남기지 않습니다. `HttpRequestException` 등 URL이 담긴 예외는 `InnerException`으로 붙이지 않습니다.
- `BizgoApiException.Body`에는 응답 원문(전화번호 포함 가능)이 있으니 그대로 로그에 남기지 마세요.
- `http://` base URL은 거부합니다(테스트용 localhost 제외). 사용자 정보(`user:pw@`), 쿼리, fragment가 든 base URL도 거부합니다. SDK에는 TLS 검증을 끄는 옵션이 없습니다(넘긴 핸들러의 TLS 설정은 그대로 적용됨).
- API Key는 `^[\x21-\x7e]+$`만 허용하고 앞뒤 공백·줄바꿈을 조용히 잘라내지 않습니다(설정 오류).
- 응답 본문은 압축 해제 후 16MB, JSON 중첩은 64단계까지만 읽습니다(웹훅도 64단계, 본문 1MB). 웹훅 `X-IB-Timestamp`는 앞뒤 공백 없이 ASCII 숫자 1~16자리만 받습니다. 빈 값·공백뿐인 secret과 0 이하 tolerance는 어디서나 `BizgoConfigurationException`입니다.
- 강력한 이름(strong name) 서명은 하지 않습니다. .NET Core/.NET 5+는 강력한 이름을 검증하지 않아 보안 효과가 없고, 키를 한 번 정하면 어셈블리 ID가 영구히 묶입니다. 강력한 이름이 필요한 .NET Framework 프로젝트에서 문제가 되면 Issue로 알려 주세요(그때는 공개 키만 커밋하는 public signing을 검토합니다).
- `System.Net.Http` 자체 로깅(`System.Net.Http` EventSource, `HttpClientFactory` 로거)은 요청 URL(쿼리 포함)을 남길 수 있습니다. 운영에서는 해당 카테고리를 Warning 이상으로 두세요.
- 취약점 신고는 [SECURITY.md](SECURITY.md)를 참고하세요.

## 이전 SDK에서 옮기기

NuGet 패키지 이름이 바뀌었습니다: `Infobank.Omni.Sdk.Cs` → **`Bizgo.Sdk.Comm`**. API 자체가 다르므로 호환되지 않습니다.

| Infobank.Omni.Sdk.Cs | Bizgo.Sdk.Comm |
|---|---|
| `https://omni.ibapi.kr`, `new InfobankClient(clientId, clientSecret)` + `Init()`으로 토큰 발급 | `https://mars.ibapi.kr`, `new BizgoClient(...)` + API Key 하나 (토큰 발급 없음) |
| `SmsRequest.Builder()...Build()` + `client.SendMessage(request)` (동기) | `await client.Send.SmsAsync(to, from, text)` / `OmniAsync(to, messages)` |
| 채널별 요청 클래스(`SmsRequest`, `AlimTalkRequest`, `OmniMsgRequest` ...) | 채널 메시지 모델(`SmsMessage`, `AlimtalkMessage` ...)을 `messages`에 순서대로 |
| `ApiResponse?`의 `IsSuccess` 확인 | 실패는 예외(`BizgoApiException` 계층), 수신자별 결과는 `result.Failed` |
| `ReceivePollingReport()` + `DeleteReport(id)` | `Reports.ConsumeAsync(handler)` (성공한 배치만 ack) |
| `FileUpload(serviceType, path)` | `Files.UploadMmsAsync` / `UploadRcsAsync` / `UploadBrandMessageAsync` |
| `SetSDKLogFactory(ILoggerFactory)`, Newtonsoft.Json 의존 | `BizgoClientOptions.Log` 콜백, net8.0에서 의존성 없음 |
| Form API(`MessageFormRequest`) | 없음 (알림톡·브랜드메시지는 템플릿 자동 치환 발송 사용) |

## 개발

.NET 10 SDK(`global.json`)가 필요합니다. 테스트는 네트워크·API Key 없이 가짜 `HttpMessageHandler`로 실행됩니다.

```bash
dotnet build -c Release
dotnet test -c Release                                    # net8.0, net10.0 (Windows에서는 net48도)
dotnet format --verify-no-changes
dotnet run --project tools/Bizgo.Codegen                  # spec/openapi.yaml이 바뀌었을 때 모델·리소스·웹훅 재생성
dotnet run --project tools/Bizgo.Codegen -- --check       # CI: 생성 결과가 커밋과 같은지
dotnet list package --vulnerable --include-transitive
dotnet pack src/Bizgo.Sdk.Comm -c Release -o artifacts
dotnet pack src/Bizgo.Sdk.Comm.Testing -c Release -o artifacts
```

AI 코딩 도구로 이 저장소를 수정할 때의 규칙은 [AGENTS.md](AGENTS.md)에 있습니다.
SDK를 **사용하는** 코드를 AI로 작성할 때는 [llms.txt](llms.txt)를 컨텍스트로 넣으면 정확도가 높아집니다.

## 라이선스

[Apache-2.0](LICENSE)
