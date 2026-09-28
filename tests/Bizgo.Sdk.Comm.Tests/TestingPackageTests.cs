using System;
using System.Linq;
using System.Threading.Tasks;
using Bizgo.Models;
using Bizgo.Testing;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>The Bizgo.Sdk.Comm.Testing package (SDK-DESIGN §11.3).</summary>
    public class TestingPackageTests
    {
        private const string Secret = "test-webhook-secret-not-real";

        [Fact]
        public async Task Default_send_response_accepts_every_recipient_and_is_recorded()
        {
            using var fake = new FakeBizgoHandler();
            using var client = fake.CreateClient();
            var result = await client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone }, Placeholders.Phone, "hello", new SendOptions { Ref = "order-1" });

            Assert.Equal(2, result.MsgKeys.Count);
            Assert.Equal("order-1", result.Ref);
            var request = fake.LastRequest!;
            Assert.Equal("sendOmni", request.OperationId);
            Assert.Equal("send.omni", request.Operation);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/api/comm/v1/send/omni", request.PathTemplate);
            Assert.Equal("hello", (string)request.Json!["messageFlow"]![0]!["sms"]!["text"]!);
            Assert.Contains("Authorization", request.HeaderNames);
            Assert.DoesNotContain(Placeholders.Phone, request.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(FakeBizgoHandler.FakeApiKey, request.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_reservation_returns_a_fake_resvKey()
        {
            using var fake = new FakeBizgoHandler();
            using var client = fake.CreateClient();
            var request = new ReservationCreateRequest
            {
                Destinations = new[] { new Destination(Placeholders.Phone) },
                MessageFlow = new[] { ReservationMessageFlowItem.Of(new SmsMessage { From = Placeholders.Phone, Text = "hello" }) },
                ResvSendTime = "2099-01-01T09:00:00",
            };
            var result = await client.Reservations.CreateAsync(request);
            Assert.StartsWith("FAKE-RESV-KEY-", result.ResvKey, StringComparison.Ordinal);
            Assert.Single(result.Data!.Destinations!);
        }

        [Fact]
        public async Task Errors_are_injected_per_call_then_defaults_resume()
        {
            using var fake = new FakeBizgoHandler();
            using var client = fake.CreateClient();
            fake.On("sendOmni").RateLimited(retryAfterSeconds: 1).Fail(BizgoErrorLayer.Gateway, 401, "A401").SendResult("A000", "A306");

            var limited = await Assert.ThrowsAsync<BizgoRateLimitException>(() => client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));
            Assert.Equal(TimeSpan.FromSeconds(1), limited.RetryAfter);
            await Assert.ThrowsAsync<BizgoAuthenticationException>(() => client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));
            var partial = await client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone }, Placeholders.Phone, "x");
            Assert.Equal("A306", partial.Failed.Single().Code);
            var ok = await client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x");
            Assert.Empty(ok.Failed);

            fake.On("sendOmni").SendDuplicates().SendResult("A000", "A301");
            var repeated = await client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone }, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-1" });
            Assert.Equal(2, repeated.Duplicates.Count);
            Assert.Empty(repeated.Failed);
            Assert.Empty(repeated.Succeeded);
            var mixed = await client.Send.SmsAsync(new[] { Placeholders.Phone, Placeholders.OtherPhone }, Placeholders.Phone, "x", new SendOptions { IdempotencyKey = "k-2" });
            Assert.Single(mixed.Duplicates);
            Assert.Single(mixed.Succeeded);

            fake.On("POST", "/api/comm/v1/send/omni").ConnectionError();
            await Assert.ThrowsAsync<BizgoConnectionException>(() => client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x"));
        }

        [Fact]
        public async Task Data_stubs_uploads_and_unknown_paths()
        {
            using var fake = new FakeBizgoHandler();
            using var client = fake.CreateClient();
            fake.On("GET", "/api/comm/v1/report/inquiry/{msgKey}").RespondData("{\"report\":[{\"msgKey\":\"KEY001\",\"result\":\"Success\"}]}");
            var reports = await client.Reports.InquiryAsync("KEY001");
            Assert.Equal("KEY001", reports.Single().MsgKey);
            Assert.Empty(await client.Reports.InquiryAsync("KEY002")); // default: empty data

            var upload = await client.Files.UploadMmsAsync(FileUpload.FromBytes(new byte[] { 1, 2, 3 }, "a.jpg"), fileKey: "FILE_KEY_001");
            Assert.StartsWith("FAKE-FILE-KEY-", upload.FileKey, StringComparison.Ordinal);
            var form = fake.LastRequest!.Form!;
            Assert.Equal(3, form["file"].Size);
            Assert.Equal("a.jpg", form["file"].FileName);
            Assert.Equal("FILE_KEY_001", form["fileKey"].Text);

            // generated operations get the default envelope too (empty object / empty list, never null)
            var templates = await client.Alimtalk.Templates.ListAsync("SENDER_KEY_EXAMPLE");
            Assert.Empty(templates);
            Assert.Equal("listAlimtalkTemplates", fake.LastRequest!.OperationId);
            Assert.Equal("SENDER_KEY_EXAMPLE", fake.LastRequest.Query["senderKey"].Single());

            Assert.Throws<ArgumentException>(() => fake.On("noSuchOperation"));
        }

        [Fact]
        public void Signed_webhooks_verify_with_the_receiver()
        {
            var receiver = new WebhookReceiver(Secret);
            var signed = WebhookSigner.Sign(Secret, "{\"msgKey\":\"KEY001\",\"code\":\"A000\"}");
            var report = receiver.Report(signed.Headers, signed.BodyBytes);
            Assert.Equal("KEY001", report.MsgKey);
            Assert.Equal("KEY001", receiver.Report(signed.Header, signed.BodyBytes).MsgKey);

            var other = WebhookSigner.Sign("another-secret-not-real", signed.Body);
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Report(other.Headers, other.Body));
            Assert.DoesNotContain(Secret, signed.ToString(), StringComparison.Ordinal);
        }
    }
}
