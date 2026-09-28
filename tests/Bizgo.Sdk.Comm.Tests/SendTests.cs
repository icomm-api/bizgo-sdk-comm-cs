using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    public class SendTests
    {
        private const string SendPath = "/api/comm/v1/send/omni";

        [Fact]
        public async Task Sms_request_body_and_headers()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            var result = await t.Client.Send.SmsAsync(Placeholders.Phone, from: Placeholders.Phone, text: "인증번호는 123456 입니다.", options: new SendOptions { Ref = "ref-1" });

            var request = t.Handler.Last;
            Assert.Equal(Placeholders.ApiKey, request.Headers["Authorization"]); // raw key, no prefix (verified on sandbox)
            Assert.StartsWith("bizgo-sdk-comm-cs/", request.Headers["User-Agent"], StringComparison.Ordinal);
            Assert.Equal("application/json", request.Headers["Accept"]);
            Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
            Assert.Equal("https://sandbox-mars.ibapi.kr" + SendPath, request.Uri.ToString());
            JsonAssert.Equal(
                "{\"destinations\":[{\"to\":\"01000000000\"}],\"messageFlow\":[{\"sms\":{\"from\":\"01000000000\",\"text\":\"인증번호는 123456 입니다.\"}}],\"ref\":\"ref-1\"}",
                request.Json);
            Assert.Equal(new[] { "KEY000" }, result.MsgKeys);
            Assert.Empty(result.Failed);
            Assert.Equal("ref-1", result.Ref);
            Assert.Equal("TR-TEST", result.TrackingId);
        }

        [Fact]
        public async Task Fallback_flow_keeps_order_and_omits_unset_defaults()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.OmniAsync(
                new Destination(Placeholders.Phone) { ReplaceWords = new Dictionary<string, string> { ["name"] = "홍길동" } },
                new IChannelMessage[]
                {
                    new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, MsgType = "AT", Text = "#{name}님 주문 완료" },
                    new SmsMessage { From = Placeholders.Phone, Text = "#{name}님 주문 완료" },
                },
                new SendOptions { IdempotencyKey = "order-1" });

            var body = t.Handler.Last.Json;
            var flow = body["messageFlow"]!.AsArray();
            Assert.Equal(new[] { "alimtalk", "sms" }, flow.Select(item => item!.AsObject().Single().Key).ToArray());

            // responseMethod / timeout have spec defaults; they are not sent
            JsonAssert.Equal(
                "{\"senderKey\":\"SENDER_KEY_EXAMPLE\",\"templateCode\":\"TEMPLATE_CODE_EXAMPLE\",\"msgType\":\"AT\",\"text\":\"#{name}님 주문 완료\"}",
                flow[0]!["alimtalk"]);
            JsonAssert.Equal("[{\"to\":\"01000000000\",\"replaceWords\":{\"name\":\"홍길동\"}}]", body["destinations"]);
            Assert.Equal("order-1", (string?)body["idempotencyKey"]);
        }

        [Fact]
        public async Task Partial_failure_is_reported_per_recipient()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000", "A306")));

            var result = await t.Client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone }, Placeholders.Phone, "x");

            Assert.Equal(new[] { "A306" }, result.Failed.Select(d => d.Code).ToArray());
            Assert.Single(result.Succeeded);
            Assert.Equal(2, result.Destinations.Count);
        }

        // SDK-DESIGN §12 rule 4: a repeated idempotencyKey comes back as HTTP 200 with per-recipient A301 (sandbox 2026-09-28)
        [Fact]
        public async Task Per_recipient_A301_is_a_duplicate_not_a_failure()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A301", "A301")));

            var result = await t.Client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone }, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-1" });

            Assert.Equal(2, result.Duplicates.Count);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Succeeded);
            Assert.Empty(result.MsgKeys);
            Assert.Equal("SendResult(Succeeded=0, Failed=0, Duplicates=2)", result.ToString());
        }

        [Fact]
        public async Task Mixed_response_splits_succeeded_failed_and_duplicates()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000", "A306", "A301")));

            var result = await t.Client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone, Placeholders.Phone }, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-1" });

            Assert.Equal(new[] { "A000" }, result.Succeeded.Select(d => d.Code).ToArray());
            Assert.Equal(new[] { "A306" }, result.Failed.Select(d => d.Code).ToArray());
            Assert.Equal(new[] { "A301" }, result.Duplicates.Select(d => d.Code).ToArray());
            Assert.Equal(new[] { "KEY000" }, result.MsgKeys);
            Assert.DoesNotContain(Placeholders.Phone, string.Join(",", result.Duplicates.Select(d => d.ToString())), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Mms_and_lms()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.MmsAsync(Placeholders.Phone, Placeholders.Phone, "본문", new[] { "FILE_KEY_001" }, title: "제목");
            JsonAssert.Equal("[\"FILE_KEY_001\"]", t.Handler.Last.Json["messageFlow"]![0]!["mms"]!["fileKey"]);
            Assert.Equal("제목", (string?)t.Handler.Last.Json["messageFlow"]![0]!["mms"]!["title"]);

            await t.Client.Send.LmsAsync(Placeholders.Phone, Placeholders.Phone, "본문");
            var mms = t.Handler.Last.Json["messageFlow"]![0]!["mms"]!.AsObject();
            Assert.False(mms.ContainsKey("fileKey"));
            Assert.False(mms.ContainsKey("title"));
        }

        [Theory]
        [InlineData(46, false, "최대 90byte")]
        [InlineData(0, true, "EUC-KR")]
        public async Task Sms_byte_limit_is_checked_before_sending(int hangul, bool emoji, string reason)
        {
            using var t = new TestClient();
            var text = emoji ? "안녕\U0001F600" : new string('가', hangul);

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, text));

            Assert.Contains(reason, error.Message, StringComparison.Ordinal);
            Assert.Equal("messageFlow[0].sms.text", error.Errors.Single().Path);
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public void Sms_limit_counts_bytes_not_characters()
        {
            new SmsMessage { From = Placeholders.Phone, Text = new string('가', 45) }.Validate(); // 90 bytes: ok
            new SmsMessage { From = Placeholders.Phone, Text = new string('a', 90) }.Validate();
            new MmsMessage { From = Placeholders.Phone, Text = new string('가', 1000) }.Validate(); // 2,000 bytes: ok
            Assert.Throws<BizgoValidationException>(() => new MmsMessage { From = Placeholders.Phone, Text = new string('가', 1001) }.Validate());
            Assert.Throws<BizgoValidationException>(() => new SmsMessage { From = Placeholders.Phone, Text = new string('a', 91) }.Validate());
        }

        [Fact]
        public void Unknown_fields_are_rejected()
        {
            var json = "{\"destinations\":[{\"to\":\"01000000000\"}],\"messageFlow\":[{\"alimtalk\":{\"senderKey\":\"S\",\"templateCode\":\"T\",\"templatecode\":\"typo\"}}]}";
            var error = Assert.Throws<BizgoValidationException>(() => SendOmniRequest.FromJson(json));
            Assert.Contains("모델에 없는 필드", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task More_than_200_recipients_is_rejected()
        {
            using var t = new TestClient();
            var many = Enumerable.Range(0, 201).Select(_ => Placeholders.Phone).ToArray();

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.SmsAsync(many, Placeholders.Phone, "x"));

            Assert.Contains("최대 200개", error.Message, StringComparison.Ordinal);
            Assert.Empty(t.Handler.Requests);
        }

        [Theory]
        [InlineData("{\"sms\":{\"from\":\"1\",\"text\":\"x\"},\"mms\":{\"from\":\"1\",\"text\":\"x\"}}")]
        [InlineData("{}")]
        public void Message_flow_item_needs_exactly_one_channel(string item)
        {
            var json = "{\"destinations\":[{\"to\":\"01000000000\"}],\"messageFlow\":[" + item + "]}";
            var error = Assert.Throws<BizgoValidationException>(() => SendOmniRequest.FromJson(json));
            Assert.Contains("정확히 하나", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Required_fields_enums_and_ranges_are_validated()
        {
            var request = new SendOmniRequest
            {
                Destinations = new List<Destination> { new Destination() },
                MessageFlow = new List<MessageFlowItem>
                {
                    MessageFlowItem.Of(new AlimtalkMessage { SenderKey = "S", TemplateCode = "T", MsgType = "XX" }),
                    MessageFlowItem.Of(new RcsMessage { From = "1", FormatId = "F", BrandKey = "B", Body = new RcsBody(), ExpiryOption = "9" }),
                },
                IdempotencyTtl = 86401,
            };

            var error = Assert.Throws<BizgoValidationException>(() => request.Validate());

            var paths = error.Errors.Select(e => e.Path).ToArray();
            Assert.Contains("destinations[0].to", paths);
            Assert.Contains("messageFlow[0].alimtalk.msgType", paths);
            Assert.Contains("messageFlow[1].rcs.expiryOption", paths);
            Assert.Contains("idempotencyTtl", paths);
        }

        [Fact]
        public async Task Validation_errors_do_not_echo_phone_numbers()
        {
            using var t = new TestClient();

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.SmsAsync(Placeholders.OtherPhone, Placeholders.OtherPhone, "\U0001F600"));

            Assert.DoesNotContain(Placeholders.OtherPhone, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("\U0001F600", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Send_without_idempotency_key_is_not_retried_after_timeout()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Throw(new TaskCanceledException("simulated timeout")));

            await Assert.ThrowsAsync<BizgoTimeoutException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));

            Assert.Equal(1, t.Handler.Count(HttpMethod.Post, SendPath));
        }

        [Fact]
        public async Task Send_without_idempotency_key_is_not_retried_after_5xx()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, _ => Responses.Text("<html>bad gateway</html>", HttpStatusCode.BadGateway));

            await Assert.ThrowsAsync<BizgoInternalServerException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));

            Assert.Equal(1, t.Handler.Count(HttpMethod.Post, SendPath));
        }

        [Fact]
        public async Task Send_with_idempotency_key_is_retried_after_timeout()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Throw(new TaskCanceledException("simulated timeout")), Responses.Ok(Responses.Accepted("A000")));

            var result = await t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-1" });

            Assert.Equal(2, t.Handler.Count(HttpMethod.Post, SendPath));
            Assert.Equal(new[] { "KEY000" }, result.MsgKeys);
            Assert.Equal(t.Handler.Requests[0].BodyText, t.Handler.Requests[1].BodyText); // same bytes on retry
        }

        [Fact]
        public async Task Duplicate_after_lost_response_explains_what_happened()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Throw(new HttpRequestException("reset")), Responses.Ok(Responses.Envelope(code: "A301")));

            var error = await Assert.ThrowsAsync<BizgoDuplicateRequestException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-1" }));

            Assert.Contains("이미 접수", error.Message, StringComparison.Ordinal);
            Assert.Equal("A301", error.Code);
            Assert.Equal(BizgoErrorLayer.Service, error.Layer);
        }

        [Fact]
        public async Task Duplicate_without_connection_error_keeps_server_message()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Envelope(code: "A301")));

            var error = await Assert.ThrowsAsync<BizgoDuplicateRequestException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-1" }));

            Assert.DoesNotContain("이전 시도", error.Message, StringComparison.Ordinal);
            Assert.Equal("이미 동일한 요청이 처리되었습니다.", error.Description);
        }

        [Fact]
        public async Task Rate_limit_is_retried_with_retry_after()
        {
            using var t = new TestClient();
            t.Handler.On(
                HttpMethod.Post,
                SendPath,
                _ =>
                {
                    var limited = Responses.Json("{\"common\":{\"authCode\":\"A020\",\"authResult\":\"Ratelimit\"}}", (HttpStatusCode)429);
                    limited.Headers.TryAddWithoutValidation("Retry-After", "2");
                    return limited;
                },
                Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x");

            Assert.Equal(2, t.Handler.Count(HttpMethod.Post, SendPath));
            Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, t.Delays.ToArray());
        }

        [Fact]
        public async Task Retry_after_is_capped_at_60_seconds_and_backoff_is_exponential()
        {
            using var t = new TestClient(o => o.MaxRetries = 3);
            t.Handler.On(
                HttpMethod.Get,
                "/api/comm/v1/report/polling",
                _ =>
                {
                    var limited = Responses.Json("{}", (HttpStatusCode)429);
                    limited.Headers.TryAddWithoutValidation("Retry-After", "3600");
                    return limited;
                },
                _ => Responses.Text("x", HttpStatusCode.ServiceUnavailable),
                _ => Responses.Text("x", HttpStatusCode.ServiceUnavailable),
                Responses.Ok(Responses.Envelope("{\"reportId\":\"\",\"report\":null}")));

            await t.Client.Reports.PollAsync();

            Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, t.Delays.ToArray());
        }

        [Fact]
        public async Task Rate_limit_gives_up_after_max_retries()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, _ => Responses.Json("{\"data\":{\"code\":\"A020\",\"result\":\"Number of 'Ratelimit' exceeded\"}}", (HttpStatusCode)429));

            var error = await Assert.ThrowsAsync<BizgoRateLimitException>(() => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));

            Assert.Equal(3, t.Handler.Count(HttpMethod.Post, SendPath)); // 1 + MaxRetries(2)
            Assert.Null(error.RetryAfter);
        }

        [Fact]
        public async Task Concurrent_sends_share_one_client_safely()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x")));

            Assert.All(results, r => Assert.Single(r.MsgKeys));
            Assert.Equal(20, t.Handler.Requests.Count);
            Assert.All(t.Handler.Requests, r => Assert.Equal(Placeholders.ApiKey, r.Headers["Authorization"]));
        }

        [Fact]
        public void Unknown_channel_message_implementation_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => MessageFlowItem.Of(new ForeignMessage()));
        }

        private sealed class ForeignMessage : IChannelMessage
        {
            public string ChannelKey => "sms";
        }
    }
}
