# 예제

모든 예제는 **sandbox**(실제 발송 없음)에 연결하고, 값은 환경변수에서 읽습니다.

```bash
export BIZGO_API_KEY=...            # 콘솔 > 발송관리 > 연동관리 (코드에 쓰지 마세요)
export BIZGO_FROM=...               # 등록한 발신번호
export BIZGO_TO=...                 # 테스트 수신번호
dotnet run --project examples/Bizgo.Examples -- send-sms
```

| 이름 | 클래스 | 내용 |
|---|---|---|
| `send-sms` | `SendSms` | SMS 발송과 수신자별 접수 결과 확인 |
| `send-alimtalk-fallback` | `SendAlimtalkFallback` | 알림톡 발송, 실패 시 SMS 대체발송, 멱등성 키 (`BIZGO_KAKAO_SENDER_KEY`, `BIZGO_KAKAO_TEMPLATE_CODE`) |
| `send-mms` | `SendMms` | 이미지 업로드 후 MMS 발송 (`BIZGO_IMAGE`) |
| `poll-reports` | `PollReports` | 리포트 Polling 처리(처리 성공 시에만 수신 확인) |
| `message-history` | `MessageHistory` | 발송 이력 전체 조회(페이지 자동 순회)와 상태 조회 |
| `concurrent-send` | `ConcurrentSend` | 클라이언트 하나로 여러 요청 동시 발송 |
| `bulk-send` | `BulkSend` | 대량 발송(200명씩, 동시 4개, 멱등성 키 접두어, 실패 청크 확인) |
| `alimtalk-templates` | `AlimtalkTemplates` | 생성된 리소스 사용: 알림톡 템플릿 전체 순회와 단건 조회 (`BIZGO_KAKAO_SENDER_KEY`) |
| (테스트 전용) | `CounselWebhook` | 상담톡 웹훅 처리(secret 없이 `Webhooks.ParseCounselMessage`, `{"code":"A000","result":"Success"}` 응답) |
| `webhook-server` | `WebhookServer` | 리포트 웹훅 수신 서버(서명 검증, 중복 처리, 5초 안에 응답) (`BIZGO_WEBHOOK_SECRET`) |

코드는 [Bizgo.Examples/Examples.cs](Bizgo.Examples/Examples.cs)에 있습니다. 각 예제의 `RunAsync()`/`Handle()`은 테스트(`tests/Bizgo.Sdk.Comm.Tests/ExamplesTests.cs`)에서 가짜 서버로 실행되므로, 예제 코드는 항상 현재 SDK와 맞습니다.
