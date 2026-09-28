# 보안 정책

## 취약점 신고

보안 취약점은 **공개 Issue로 올리지 마세요.** GitHub의 [Private vulnerability reporting](../../security/advisories/new)으로 비공개 신고해 주세요.
확인 후 영업일 기준 5일 안에 답변합니다.

지원 버전: 최신 minor 버전(현재 1.2.x)에 보안 수정을 제공합니다.

## 자격 증명이 노출됐을 때

1. 즉시 비즈고 콘솔 `발송관리 > 연동관리`에서 해당 API Key를 폐기하고 새로 발급합니다. 저장소 기록에서 지워도 이미 복제됐을 수 있으므로 **폐기가 먼저**입니다.
2. 웹훅 secret은 비즈고에 재발급을 요청합니다.
3. 허용 IP(ACL) 목록을 점검합니다.

## SDK의 보안 동작

- API Key는 `Authorization` 헤더로만 보내고, `ToString()`·로그·hook·예외 메시지에 넣지 않습니다. 헤더 주입을 막기 위해 `^[\x21-\x7e]+$`가 아닌 키(공백·줄바꿈·제어 문자·ASCII 밖 문자)는 잘라내지 않고 거부합니다.
- SDK는 `HttpClient`를 직접 만들고 소유하며, 완성된 `HttpClient`는 받지 않습니다. `Authorization`, `User-Agent`, `X-Bizgo-Client`는 요청마다 SDK가 설정합니다.
- 넘겨받은 `HttpMessageHandler`(및 `DelegatingHandler` 체인)의 `HttpClientHandler`/`SocketsHttpHandler`가 자체 인증(`Credentials`, `UseDefaultCredentials`, `PreAuthenticate`), 자동 리다이렉트(`AllowAutoRedirect`), 쿠키(`UseCookies`)를 쓰면 설정 오류입니다. 자체 인증은 401 뒤 발송 요청을 다른 인증 정보로 다시 보내고(중복 발송), 자동 리다이렉트는 전화번호가 든 본문을 다른 호스트로 다시 보낼 수 있습니다. 검사할 수 없는 핸들러 형식은 base URL 밖에서 온 응답을 거부하는 것으로만 막습니다.
- hooks·OpenTelemetry span에는 operation, 경로 템플릿, 상태, 코드, 시도 횟수, 소요 시간만 넘깁니다. `async` hook·log 콜백은 거부합니다(잡을 수 없는 `async void` 예외로 프로세스가 종료되는 것을 막기 위해).
- 모델·옵션의 `ToString()`은 전화번호 필드(`to`, `from`, `phoneNumber`, `phone_number` 등)를 가리고(`010****0000`), 사람 이름·닉네임·이메일·주소(`userName`, `nickname`, `email`, `roadAddress`, `detailAddress`)는 첫 글자만 보여 주며, 본문(`text`, `message`, `content`, `comment`, `extra`와 이름이 Message/Content/Text/Comment로 끝나는 문자열)은 길이만 보여 줍니다. `AdditionalProperties`는 넣지 않습니다.
- 요청 본문, 쿼리 문자열, 검증 오류의 입력값(전화번호 포함 가능)을 로그·예외에 넣지 않습니다. URL이 담긴 HTTP 라이브러리 예외는 `InnerException`으로 연결하지 않습니다.
- `BizgoApiException.Body`에는 응답 원문이 있습니다. 전화번호가 있을 수 있으니 그대로 로그에 남기지 마세요.
- https가 아닌 base URL(테스트용 localhost 제외), 사용자 정보·쿼리·fragment가 든 base URL은 거부합니다.
- **TLS**: SDK 자체에는 TLS 검증을 끄는 옵션이 없습니다. SDK가 만드는 핸들러는 운영체제의 기본 인증서 검증을 씁니다. `HttpMessageHandler`를 넘기면 **그 핸들러의 TLS 설정(인증서 검증 콜백, 클라이언트 인증서, 프로토콜)이 그대로 적용**되므로, 검증을 끄는 콜백(`ServerCertificateCustomValidationCallback`이 항상 true 등)을 넣지 마세요.
- 리다이렉트를 따르거나 재시도하지 않습니다. 3xx 응답은 원래 상태(`HTTP 307` 등)를 담은 `BizgoInvalidResponseException`이고, base URL 밖에서 온 응답도 거부합니다.
- 업로드 파일 경로가 비었거나 잘못됐거나(NUL, `|` 등) 너무 길거나 읽을 수 없으면 `BizgoValidationException`이며, 메시지에는 파일 이름만 넣습니다(디렉터리 경로 없음).
- 예외는 `[Serializable]`이 아닙니다(`BinaryFormatter` 폐기). 프로세스 간 전달은 `ToJson()`/`FromJson()`을 쓰고, 잘못된 JSON은 `ArgumentException`입니다.
- 어셈블리는 강력한 이름으로 서명하지 않습니다(.NET 5+에서는 검증되지 않아 보안 효과가 없음). 비밀 키를 저장소에 두지 않습니다.
- 응답 본문은 압축 해제 후 16MB, 업로드 파일은 10MB(MMS는 300KB), 웹훅 본문은 1MB까지만 읽고, JSON 중첩은 응답·웹훅 모두 64단계까지만 허용합니다.
- 경로 값은 한 세그먼트로 인코딩하고(`/`, `?`, `#`, `%` 포함) `.`/`..`는 거부합니다. 헤더 파라미터 값은 한 줄 ASCII만 허용합니다.
- 발송은 `IdempotencyKey`가 없으면 타임아웃·5xx 후 자동 재시도하지 않습니다(중복 발송 방지).
- 웹훅 서명은 `CryptographicOperations.FixedTimeEquals`(netstandard2.0에서는 같은 방식의 구현)로 비교하고 timestamp 허용 오차(기본 300초)를 벗어난 오래된 요청을 거부합니다.
  운영 환경에서는 HTTPS, 발신 IP 허용 목록, `msgKey` 기준 중복 제거를 함께 적용하고, 중요한 판단은 조회 API로 결과를 확인하세요.
  `X-IB-Timestamp`는 앞뒤 공백 없이 ASCII 숫자 1~16자리만 받고, 빈 값·공백뿐인 secret과 0 이하 tolerance는 어디서나 `BizgoConfigurationException`입니다.
- **상담톡 웹훅**: 상담톡 웹훅에는 서명이 없습니다(서명은 리포트·MO 웹훅에만 적용). SDK는 상담톡 웹훅의 서명 헤더를 요구·검사하지 않고(와도 무시), 본문 크기(1MB)·JSON 중첩(64단계)·형식 검사만 적용합니다. secret 없이 `Webhooks.ParseCounsel*`로 파싱할 수 있습니다. 상담톡 엔드포인트도 HTTPS, 비즈고 웹훅 발신 IP 허용 목록, `msgKey` 기준 중복 제거(있는 경우)를 적용하세요. 리포트·MO 웹훅은 항상 서명이 필요합니다.

## 공급망

- 런타임 의존성: net8.0은 없음(BCL만). netstandard2.0은 Microsoft의 `System.Text.Json`, `System.Text.Encoding.CodePages`, `Microsoft.Bcl.AsyncInterfaces`. OpenTelemetry용 `ActivitySource`는 net8.0 BCL만 쓰며, netstandard2.0에는 `System.Diagnostics.DiagnosticSource`를 추가하지 않으려고 span을 만들지 않습니다. `Bizgo.Sdk.Comm.Testing`은 SDK 외 의존성이 없고 테스트 프로젝트에서만 참조합니다.
- `nuget.config`는 nuget.org만 쓰고(package source mapping), 버전은 `Directory.Packages.props`와 `packages.lock.json`으로 고정합니다. CI는 locked mode로 복원합니다.
- NuGet 배포는 GitHub Actions에서 NuGet Trusted Publishing(OIDC, 단기 키)으로만 하며, 저장소에 NuGet API 키를 두지 않습니다. 배포 job은 필수 승인자가 있는 `nuget` 환경에서만 실행됩니다.
- 패키지는 SourceLink·결정적 빌드·심볼 패키지(.snupkg)를 포함합니다.
- 모든 커밋과 PR은 gitleaks로 검사하고, 의존성은 `dotnet list package --vulnerable`과 NuGet Audit으로 검사합니다.
