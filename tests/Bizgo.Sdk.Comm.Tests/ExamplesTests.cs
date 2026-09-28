using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Bizgo.Examples;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>Runs every example against the fake server so the examples never drift from the SDK.</summary>
    public class ExamplesTests
    {
        private static readonly string Accepted = Responses.Envelope("{\"destinations\":[{\"to\":\"01000000000\",\"msgKey\":\"K001\",\"code\":\"A000\",\"result\":\"Success\"}]}");

        [Fact]
        public async Task Send_sms()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/send/omni", Accepted);
            await SendSms.RunAsync(t.Client, Placeholders.Phone, Placeholders.Phone);
            Assert.Equal("signup-otp", (string?)t.Handler.Last.Json["ref"]);
        }

        [Fact]
        public async Task Send_alimtalk_fallback()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/send/omni", Accepted);
            await SendAlimtalkFallback.RunAsync(t.Client, Placeholders.Phone, Placeholders.Phone, Placeholders.SenderKey, Placeholders.TemplateCode);
            var body = t.Handler.Last.Json;
            Assert.Equal(new[] { "alimtalk", "sms" }, body["messageFlow"]!.AsArray().Select(m => m!.AsObject().Single().Key).ToArray());
            Assert.False(string.IsNullOrEmpty((string?)body["idempotencyKey"]));
        }

        [Fact]
        public async Task Send_alimtalk_fallback_handles_duplicates()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/send/omni", Responses.Envelope(code: "A301"));
            await SendAlimtalkFallback.RunAsync(t.Client, Placeholders.Phone, Placeholders.Phone, Placeholders.SenderKey, Placeholders.TemplateCode);
        }

        [Fact]
        public async Task Send_mms()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/file/mms", Responses.Envelope("{\"fileKey\":\"FILE_KEY_001\"}"));
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/send/omni", Accepted);
            await SendMms.RunAsync(t.Client, Placeholders.Phone, Placeholders.Phone, new byte[] { 0xFF, 0xD8 });
            JsonAssert.Equal("[\"FILE_KEY_001\"]", t.Handler.Last.Json["messageFlow"]![0]!["mms"]!["fileKey"]);
        }

        [Fact]
        public async Task Poll_reports()
        {
            using var t = new TestClient();
            t.Handler.On(
                HttpMethod.Get,
                "/api/comm/v1/report/polling",
                Responses.Ok(Responses.Envelope("{\"reportId\":\"R1\",\"report\":[{\"msgKey\":\"K001\",\"reportCode\":\"10000\"}]}")),
                Responses.Ok(Responses.Envelope("{\"reportId\":\"\",\"report\":null}")));
            t.Handler.OnJson(HttpMethod.Delete, "/api/comm/v1/report/polling/R1", Responses.Envelope());
            await PollReports.RunAsync(t.Client);
            Assert.Equal("delivered", PollReports.Store["K001"]);
        }

        [Fact]
        public async Task Message_history()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/message/history", Responses.Envelope("{\"messages\":[{\"msgKey\":\"K001\",\"reportCode\":\"63020\"}],\"hasNext\":false}"));
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/message/inquiry/msgKey/K001", Responses.Envelope("{\"messages\":[{\"msgKey\":\"K001\",\"serviceType\":\"ALIMTALK\",\"reportCode\":\"63020\"}]}"));
            await MessageHistory.RunAsync(t.Client);
            Assert.Equal(1, t.Handler.Count(HttpMethod.Get, "/api/comm/v1/message/inquiry/msgKey/K001"));
        }

        [Fact]
        public async Task Concurrent_send()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/send/omni", Accepted);
            await ConcurrentSend.RunAsync(t.Client, Placeholders.Phone, new[] { new[] { Placeholders.Phone }, new[] { Placeholders.OtherPhone } });
            Assert.Equal(2, t.Handler.Requests.Count);
        }

        [Fact]
        public void Webhook_server()
        {
            const string Secret = "test-webhook-secret";
            var receiver = new WebhookReceiver(Secret);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var signature = string.Concat(hmac.ComputeHash(Encoding.ASCII.GetBytes(timestamp)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            var body = Encoding.UTF8.GetBytes("{\"msgKey\":\"K001\",\"serviceType\":\"SMS\",\"reportTime\":\"t\",\"reportType\":\"0\",\"reportCode\":\"10000\"}");
            var headers = new Dictionary<string, string> { ["X-IB-Timestamp"] = timestamp, ["X-IB-Signature"] = signature };

            Assert.Equal((200, "{\"msgKey\":\"K001\"}"), WebhookServer.Handle(receiver, headers, body));

            headers["X-IB-Signature"] = new string('0', 64);
            Assert.Equal((401, "{}"), WebhookServer.Handle(receiver, headers, body));
        }

        [Fact]
        public async Task Bulk_send()
        {
            using var fake = new Bizgo.Testing.FakeBizgoHandler();
            using var client = fake.CreateClient();
            var recipients = Enumerable.Range(0, 450).Select(i => "0100000" + i.ToString("0000", CultureInfo.InvariantCulture)).ToArray();
            await BulkSend.RunAsync(client, Placeholders.Phone, recipients, "example-campaign");
            Assert.Equal(3, fake.Requests.Count);
            Assert.All(fake.Requests, r => Assert.StartsWith("example-campaign-200-", (string)r.Json!["idempotencyKey"]!, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Alimtalk_templates()
        {
            using var fake = new Bizgo.Testing.FakeBizgoHandler();
            using var client = fake.CreateClient();
            fake.On("listAlimtalkTemplates")
                .RespondData("{\"alimtalk\":{\"templates\":[{\"templateCode\":\"TEMPLATE_CODE_EXAMPLE\",\"templateName\":\"안내\"}]}}")
                .RespondData("{\"alimtalk\":{\"templates\":[{\"templateCode\":\"TEMPLATE_CODE_EXAMPLE\"}]}}");
            await AlimtalkTemplates.RunAsync(client, Placeholders.SenderKey);
            Assert.Equal(new[] { "listAlimtalkTemplates", "listAlimtalkTemplates", "getAlimtalkTemplate" }, fake.Requests.Select(r => r.OperationId));
            Assert.Equal("APR", fake.Requests[0].Query["inspectionStatus"].Single());
        }

        [Fact]
        public void Counsel_webhook()
        {
            var body = "{\"msgKey\":\"K001\",\"sessionId\":\"SESSION_ID_EXAMPLE\"}";
            Assert.Equal((200, "{\"code\":\"A000\",\"result\":\"Success\"}"), CounselWebhook.Handle(System.Text.Encoding.UTF8.GetBytes(body)));
            Assert.Equal(400, CounselWebhook.Handle(System.Text.Encoding.UTF8.GetBytes("[1]")).Status);
        }

        [Fact]
        public void Missing_environment_variable_gives_a_hint()
        {
            var error = Assert.Throws<InvalidOperationException>(() => Env.Require("BIZGO_TEST_VARIABLE_THAT_IS_NOT_SET"));
            Assert.Contains("examples/README.md", error.Message, StringComparison.Ordinal);
        }
    }
}
