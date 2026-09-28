using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>Regression tests for the hardening rules of SDK-DESIGN §12 (one or more per rule) and the identification headers of §2.1.</summary>
    public class HardeningTests
    {
        private const string SendPath = "/api/comm/v1/send/omni";
        private const string Polling = "/api/comm/v1/report/polling";

        // §2.1 identification headers
        [Fact]
        public async Task Identification_headers_are_sent_and_cannot_be_overridden()
        {
            using var t = new TestClient(o => o.AppInfo = new BizgoAppInfo("myshop", "1.4.2"));
            t.Handler.OnJson(HttpMethod.Get, Polling, Responses.Envelope("{}"));
            await t.Client.Reports.PollAsync();
            var headers = t.Handler.Last.Headers;
            var userAgent = Regex.Replace(headers["User-Agent"], ",", " "); // the test handler joins product tokens with commas
            Assert.Matches(new Regex(@"^bizgo-sdk-comm-cs/[0-9A-Za-z.\-]+ (dotnet|dotnetfx)/[0-9.]+ \((linux|windows|darwin|freebsd|other); (x64|arm64|x86|arm|other)\) app/myshop-1\.4\.2$"), userAgent);
            Assert.Matches(new Regex(@"^bizgo-sdk-comm-cs/[0-9A-Za-z.\-]+$"), headers["X-Bizgo-Client"]);
            Assert.Equal(Placeholders.ApiKey, headers["Authorization"]);

            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, AppInfo = new BizgoAppInfo("my shop", "1") }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, AppInfo = new BizgoAppInfo("shop", "1\r\nX-Evil: 1") }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, AppInfo = new BizgoAppInfo(new string('a', 51), "1") }));
        }

        // §12.6 (review C1/C2): a user handler cannot authenticate, redirect or keep cookies by itself; there is no HttpClient option
        [Fact]
        public void Handlers_that_authenticate_redirect_or_keep_cookies_are_rejected()
        {
            Assert.Null(typeof(BizgoClientOptions).GetProperty("HttpClient"));

            using var redirects = new HttpClientHandler { UseCookies = false }; // AllowAutoRedirect defaults to true
            Assert.Contains("AllowAutoRedirect", Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = redirects })).Message, StringComparison.Ordinal);
            using var cookies = new HttpClientHandler { AllowAutoRedirect = false }; // UseCookies defaults to true
            Assert.Contains("UseCookies", Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = cookies })).Message, StringComparison.Ordinal);
            using var nestedRedirect = new PassThrough(new PassThrough(new HttpClientHandler { UseCookies = false }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = nestedRedirect }));

            // a correctly configured handler (proxy credentials are fine) is accepted
            using var good = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, DefaultProxyCredentials = new NetworkCredential("proxy-user", "not-a-real-password") };
            using var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = new PassThrough(good) });
            Assert.NotNull(client);

            using var credentials = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = new NetworkCredential("user", "not-a-real-password") };
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = credentials }));
            using var defaults = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = true };
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = defaults }));
            using var pre = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, PreAuthenticate = true };
            using var chained = new PassThrough(pre);
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = chained }));
        }

        [Theory]
        [InlineData("https://user:pw@mars.ibapi.kr")]
        [InlineData("https://mars.ibapi.kr/?a=1")]
        [InlineData("https://mars.ibapi.kr/#frag")]
        public void Base_url_with_userinfo_query_or_fragment_is_rejected(string url)
        {
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, BaseUrl = new Uri(url) }));
        }

        // §12.16 API key format, no silent trim
        [Theory]
        [InlineData(" test-api-key-not-real")]
        [InlineData("test-api-key-not-real\n")]
        [InlineData("test-api-key-not-real\t")]
        [InlineData("테스트")]
        public void Api_key_is_not_trimmed(string key)
        {
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = key }));
        }

        // §12.1 parse errors are SDK exceptions with status, tracking id and body; sends say they may have been accepted
        [Fact]
        public async Task Unexpected_response_shapes_are_invalid_response_errors()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, SendPath, "{\"common\":{\"authCode\":\"A000\",\"infobankTrId\":\"TR-TEST\"},\"data\":{\"code\":\"A000\",\"data\":{\"destinations\":\"oops\"}}}");
            var error = await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));
            Assert.Equal(200, error.HttpStatus);
            Assert.Equal("TR-TEST", error.TrackingId);
            Assert.NotNull(error.Body);
            Assert.Contains("접수됐을 수", error.Message, StringComparison.Ordinal);

            // not a send: no "may have been accepted"
            t.Handler.OnJson(HttpMethod.Get, Polling, "{\"common\":{\"authCode\":\"A000\"},\"data\":{\"code\":\"A000\",\"data\":{\"report\":1}}}");
            var read = await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Reports.PollAsync());
            Assert.DoesNotContain("접수됐을 수", read.Message, StringComparison.Ordinal);

            // envelope fields are optional
            t.Handler.OnJson(HttpMethod.Get, Polling, "{\"data\":{\"data\":{}}}");
            Assert.True((await t.Client.Reports.PollAsync()).IsEmpty);
        }

        // §12.8 JSON depth 64 for responses (and webhooks, below)
        [Fact]
        public async Task Deeply_nested_json_is_rejected()
        {
            using var t = new TestClient();
            var deep = new string('[', 70) + new string(']', 70);
            t.Handler.OnJson(HttpMethod.Get, Polling, "{\"common\":{\"authCode\":\"A000\"},\"data\":{\"code\":\"A000\",\"data\":{\"x\":" + deep + "}}}");
            await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Reports.PollAsync());
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseReport("{\"msgKey\":" + deep + "}"));
        }

        // §12.4 A301 after any retry (5xx, 429, connection error) carries AlreadyAccepted
        [Fact]
        public async Task Duplicate_after_a_retry_is_marked_already_accepted()
        {
            using var t = new TestClient();
            var send = new SendOptions { IdempotencyKey = "order-1" };
            t.Handler.On(HttpMethod.Post, SendPath, _ => Responses.Json("{}", HttpStatusCode.BadGateway), Responses.Ok(Responses.Envelope(code: "A301")));
            var after5xx = await Assert.ThrowsAsync<BizgoDuplicateRequestException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", send));
            Assert.True(after5xx.AlreadyAccepted);
            Assert.Contains("이미 접수", after5xx.Message, StringComparison.Ordinal);

            t.Handler.On(HttpMethod.Post, SendPath, _ => Responses.Json("{}", (HttpStatusCode)429), Responses.Ok(Responses.Envelope(code: "A301")));
            Assert.True((await Assert.ThrowsAsync<BizgoDuplicateRequestException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", send))).AlreadyAccepted);

            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Envelope(code: "A301")));
            Assert.False((await Assert.ThrowsAsync<BizgoDuplicateRequestException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", send))).AlreadyAccepted);
        }

        // §12.5 path values
        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("")]
        public async Task Dot_path_values_are_rejected(string value)
        {
            using var t = new TestClient();
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Reservations.GetAsync(value));
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Reports.InquiryAsync(value));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Path_values_are_one_encoded_segment()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/reservation/resvKey/a%2Fb%3Fc%23d%25e", Responses.Envelope("{}"));
            var reservation = await t.Client.Reservations.GetAsync("a/b?c#d%e");
            Assert.NotNull(reservation);
            Assert.Equal("/api/comm/v1/reservation/resvKey/a%2Fb%3Fc%23d%25e", t.Handler.Last.Uri.AbsolutePath);
        }

        // §12.7 webhook input
        [Fact]
        public void Webhook_timestamp_secret_and_tolerance_are_checked()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature("s", new string('1', 17), "00", now: now));
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature("s", "１２３", "00", now: now)); // non-ASCII digits
            // review C8: the timestamp is not trimmed; a blank secret or bad tolerance is the same configuration error everywhere
            var signature0 = Bizgo.Testing.WebhookSigner.Signature("s", "1700000000");
            Webhooks.VerifySignature("s", "1700000000", signature0, now: now);
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature("s", " 1700000000 ", signature0, now: now));
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature("s", "1700000000\n", signature0, now: now));
            Assert.Throws<BizgoConfigurationException>(() => Webhooks.VerifySignature("   ", "1700000000", "00", now: now));
            Assert.Throws<BizgoConfigurationException>(() => new WebhookReceiver("   "));
            Assert.Throws<BizgoConfigurationException>(() => new WebhookReceiver("   ", new WebhookReceiverOptions()));
            Assert.Throws<BizgoConfigurationException>(() => new WebhookReceiver("s", TimeSpan.Zero));
            Assert.Throws<BizgoConfigurationException>(() => new WebhookReceiver("s", TimeSpan.FromSeconds(-1)));
            Assert.Throws<BizgoConfigurationException>(() => Webhooks.VerifySignature("s", "1700000000", "00", TimeSpan.Zero, now));

            // explicit opt-out of the age check still verifies the signature
            var old = "1000000000";
            var signature = Bizgo.Testing.WebhookSigner.Signature("s", old);
            Webhooks.VerifySignature("s", old, signature, Webhooks.NoTolerance, now);
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature("s", old, signature, null, now));
        }

        [Theory]
        [InlineData("{\"msgKey\":")]
        [InlineData("[]")]
        [InlineData("{\"msgKey\":{\"nested\":true}}")]
        [InlineData("{\"reportTime\":[1,2]}")]
        public void Malformed_webhook_bodies_are_verification_errors(string body)
        {
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseReport(body));
        }

        // §12.9 Retry-After
        [Theory]
        [InlineData("5", 5.0)]
        [InlineData("0", 0.0)]
        [InlineData("1e9", 60.0)]
        [InlineData("-1", null)]
        [InlineData("NaN", null)]
        [InlineData("Infinity", null)]
        [InlineData("soon", null)]
        public void Retry_after_uses_only_finite_non_negative_values(string header, double? seconds)
        {
            Assert.Equal(seconds, Transport.ParseRetryAfter(header)?.TotalSeconds);
        }

        // §12.10 exceptions survive a JSON round trip
        [Fact]
        public void Api_exceptions_round_trip_as_json()
        {
            var body = System.Text.Json.JsonDocument.Parse("{\"data\":{\"code\":\"A301\"}}").RootElement.Clone();
            var original = new BizgoDuplicateRequestException("dup", 200, "A301", BizgoErrorLayer.Service, "TR-TEST", body, alreadyAccepted: true);
            var copy = Assert.IsType<BizgoDuplicateRequestException>(BizgoApiException.FromJson(original.ToJson()));
            Assert.Equal(original.Message, copy.Message);
            Assert.True(copy.AlreadyAccepted);
            Assert.Equal("TR-TEST", copy.TrackingId);
            Assert.Equal("A301", copy.Body!.Value.GetProperty("data").GetProperty("code").GetString());

            var rate = new BizgoRateLimitException("slow", 429, "A020", BizgoErrorLayer.Service, retryAfter: TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<BizgoRateLimitException>(BizgoApiException.FromJson(rate.ToJson())).RetryAfter);
            Assert.Throws<ArgumentException>(() => BizgoApiException.FromJson("{}"));

            // review C7: out-of-range numbers never leak OverflowException
            Assert.Throws<ArgumentException>(() => BizgoApiException.FromJson("{\"type\":\"BizgoRateLimitException\",\"httpStatus\":429,\"layer\":\"service\",\"serverMessage\":\"x\",\"retryAfterSeconds\":1e300}"));
            Assert.Throws<ArgumentException>(() => BizgoApiException.FromJson("{\"type\":\"BizgoApiException\",\"httpStatus\":1e20,\"layer\":\"service\",\"serverMessage\":\"x\"}"));
            Assert.Empty(typeof(BizgoApiException).GetCustomAttributes(typeof(SerializableAttribute), inherit: false)); // documented: ToJson/FromJson is the supported way
        }

        // §12.11 phone masking in ToString of models and parameter objects
        [Fact]
        public void Phone_numbers_are_masked_in_ToString()
        {
            Assert.Equal("010****0000", PhoneMask.Mask("01000000000"));
            Assert.Equal("158****0", PhoneMask.Mask("15880000"));
            Assert.Equal("020*****00", PhoneMask.Mask("0200000000"));
            Assert.Equal("1**", PhoneMask.Mask("123"));

            var destination = new Destination(Placeholders.OtherPhone) { Ref = "r1" };
            Assert.Equal("Destination(to=010****1234, ref=r1)", destination.ToString());
            var sms = new SmsMessage { From = Placeholders.OtherPhone, Text = "인증번호 123456" };
            Assert.DoesNotContain(Placeholders.OtherPhone, sms.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("123456", sms.ToString(), StringComparison.Ordinal);
            var request = new SendOmniRequest { Destinations = new[] { destination } };
            Assert.DoesNotContain(Placeholders.OtherPhone, request.ToString(), StringComparison.Ordinal);

            // review C3: message-like text shows only its length; a person's name and nickname are masked
            var counsel = new CounselPlainMessageRequest { UserKey = "uk", SenderKey = Placeholders.SenderKey, Message = "상담 본문 내용 (개인 상담 내용)" };
            Assert.DoesNotContain("상담", counsel.ToString(), StringComparison.Ordinal);
            Assert.Contains("message=<19 chars>", counsel.ToString(), StringComparison.Ordinal);
            var naver = new NaverTalkMessage { PartnerKey = "p", UserName = "홍길동" };
            Assert.DoesNotContain("홍길동", naver.ToString(), StringComparison.Ordinal);
            Assert.Contains("userName=홍**", naver.ToString(), StringComparison.Ordinal);
            var personal = new CounselPersonalInfo { PhoneNumber = Placeholders.OtherPhone, Nickname = "닉네임" };
            Assert.Equal("CounselPersonalInfo(phone_number=010****1234, nickname=닉**)", personal.ToString());
        }

        // §12.12/§12.17 redirects: status in the message and never retried
        [Fact]
        public async Task Redirects_are_never_retried()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Get, Polling, _ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect));
            var error = await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Reports.PollAsync());
            Assert.Contains("HTTP 307", error.Message, StringComparison.Ordinal);
            Assert.Single(t.Handler.Requests);
        }

        // §12.18 (review C5): invalid paths are validation errors with the file name only, on every runtime
        [Theory]
        [InlineData("")]
        [InlineData("bad\0name.jpg")]
        [InlineData("a|b.jpg")]
        [InlineData("a<b>.jpg")]
        public async Task Invalid_upload_paths_are_validation_errors(string name)
        {
            using var t = new TestClient();
            var directory = Path.Combine(Path.GetTempPath(), "bizgo-private-dir-" + Guid.NewGuid().ToString("N"));
            var path = name.Length == 0 ? string.Empty : directory + Path.DirectorySeparatorChar + name;
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Files.UploadMmsAsync(FileUpload.FromPath(path)));
            Assert.DoesNotContain(directory, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\0", error.Message, StringComparison.Ordinal);
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Too_long_upload_path_is_a_validation_error()
        {
            using var t = new TestClient();
            var path = Path.Combine(Path.GetTempPath(), new string('d', 40000), "photo.jpg");
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Files.UploadMmsAsync(FileUpload.FromPath(path)));
            Assert.Contains("photo.jpg", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("dddd", error.Message, StringComparison.Ordinal);
        }

        // review C6: the timeout message reflects the configured timeout (the SDK owns the HttpClient)
        [Fact]
        public async Task Timeout_message_uses_the_configured_value()
        {
            using var t = new TestClient(o => { o.Timeout = TimeSpan.FromMilliseconds(300); o.MaxRetries = 0; });
            t.Handler.On(HttpMethod.Get, Polling, _ => throw new TaskCanceledException("simulated timeout"));
            var error = await Assert.ThrowsAsync<BizgoTimeoutException>(() => t.Client.Reports.PollAsync());
            Assert.Contains("0.3초", error.Message, StringComparison.Ordinal);
        }

        // §12.15 (review C4): async lambdas would become async void and crash the process, so they are refused up front
        [Fact]
        public void Async_callbacks_are_rejected()
        {
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Hooks = new BizgoHooks { OnResponse = async _ => await Task.Yield() } }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Hooks = new BizgoHooks { OnRequest = async _ => await Task.Yield() } }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Log = async _ => await Task.Yield() }));
            Action<BizgoResponseEvent> combined = _ => { };
            combined += async _ => await Task.Yield();
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Hooks = new BizgoHooks { OnResponse = combined } }));
            using var ok = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Hooks = new BizgoHooks { OnResponse = _ => { } }, Log = _ => { } });
            Assert.NotNull(ok);
        }

        // review C9: snake_case JSON fields get PascalCase properties; the wire name is unchanged
        [Fact]
        public void Snake_case_fields_have_pascal_case_properties()
        {
            var info = Webhooks.ParseCounselPersonalInfo("{\"msgKey\":\"K\",\"personalInfo\":{\"phone_number\":\"01000000000\",\"nickname\":\"n\"}}");
            Assert.NotNull(typeof(CounselPersonalInfo).GetProperty("PhoneNumber"));
            Assert.Null(typeof(CounselPersonalInfo).GetProperty("Phone_number"));
            Assert.NotNull(typeof(CounselLastReference).GetProperty("BotEvent"));
            Assert.NotNull(typeof(CounselLastReference).GetProperty("CreatedAt"));
            var json = System.Text.Json.JsonSerializer.Serialize(new CounselPersonalInfo { PhoneNumber = Placeholders.Phone }, BizgoJsonContext.Default.CounselPersonalInfo);
            Assert.Equal("{\"phone_number\":\"01000000000\"}", json);
            Assert.NotNull(info);
        }

        // §12.18 unreadable upload files
        [Fact]
        public async Task Unreadable_upload_file_is_a_validation_error_with_the_file_name_only()
        {
            using var t = new TestClient();
            var directory = Path.Combine(Path.GetTempPath(), "bizgo-missing-" + Guid.NewGuid().ToString("N"));
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Files.UploadMmsAsync(FileUpload.FromPath(Path.Combine(directory, "photo.jpg"))));
            Assert.Contains("photo.jpg", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, error.Message, StringComparison.Ordinal);
            Assert.Empty(t.Handler.Requests);
        }

        // §12.19 success responses never give null
        [Fact]
        public async Task Success_without_data_gives_empty_results()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/center/alimtalk/template", Responses.Envelope());
            var template = await t.Client.Alimtalk.Templates.GetAsync(Placeholders.SenderKey, Placeholders.TemplateCode);
            Assert.NotNull(template);
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/center/alimtalk/template/list", Responses.Envelope("null"));
            Assert.Empty(await t.Client.Alimtalk.Templates.ListAsync(Placeholders.SenderKey));
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/file/mms", Responses.Envelope());
            Assert.NotNull(await t.Client.Files.UploadMmsAsync(FileUpload.FromBytes(new byte[] { 1 }, "a.jpg")));
        }

        // §4 on generated operations: query parameters and bodies are validated before sending, without values in messages
        [Fact]
        public async Task Generated_operations_validate_before_sending()
        {
            using var t = new TestClient();
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Alimtalk.Templates.ListAsync(Placeholders.SenderKey, new AlimtalkTemplatesListOptions { InspectionStatus = "NOPE" }));
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Kakao.Senders.CreateAsync(new KakaoSenderCreateRequest(), "TOKEN_EXAMPLE", "0100000\r\n0000"));
            Assert.DoesNotContain("0100000", error.Message, StringComparison.Ordinal);
            Assert.Empty(t.Handler.Requests);
        }

        private sealed class PassThrough : DelegatingHandler
        {
            internal PassThrough(HttpMessageHandler inner)
                : base(inner)
            {
            }
        }
    }
}
