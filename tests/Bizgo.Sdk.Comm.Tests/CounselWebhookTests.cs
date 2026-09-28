using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bizgo.Testing;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>Counsel (상담톡) webhooks carry no signature: parsed without a secret, input defenses still apply (SDK-DESIGN §11.5).</summary>
    public class CounselWebhookTests
    {
        private const string Secret = "test-webhook-secret-not-real";
        private const string Body = "{\"msgKey\":\"KEY001\",\"userKey\":\"USER_KEY_EXAMPLE\",\"senderKey\":\"SENDER_KEY_EXAMPLE\",\"contents\":[{\"comment\":\"문의\"}]}";

        [Fact]
        public void Every_spec_webhook_has_a_receiver_method()
        {
            var names = ((System.Text.Json.Nodes.JsonObject)Repo.Spec["webhooks"]!).Select(w => (string)w.Value!["post"]!["x-sdk-webhook"]!).ToList();
            Assert.Equal(9, names.Count);
            foreach (var name in names)
            {
                var method = char.ToUpperInvariant(name[0]) + name.Substring(1);
                Assert.True(typeof(WebhookReceiver).GetMethods(BindingFlags.Public | BindingFlags.Instance).Count(m => m.Name == method) >= 2, "WebhookReceiver." + method);
            }
        }

        [Fact]
        public void Counsel_webhooks_parse_without_signature_headers()
        {
            var receiver = new WebhookReceiver(Secret);
            var plain = WebhookSigner.WithoutSignature(Body);
            Assert.False(plain.Headers.ContainsKey(Webhooks.SignatureHeader));
            var message = receiver.CounselMessage(plain.Headers, plain.Body);
            Assert.Equal("USER_KEY_EXAMPLE", message.UserKey);
            Assert.Equal("문의", message.Contents!.Single().Comment);
            Assert.Equal("KEY001", receiver.CounselMessage(plain.Header, plain.BodyBytes).MsgKey);
            Assert.Equal("KEY001", receiver.CounselResult(new Dictionary<string, string>(), "{\"msgKey\":\"KEY001\"}").MsgKey);
        }

        [Fact]
        public void Counsel_webhooks_ignore_signature_headers()
        {
            var receiver = new WebhookReceiver(Secret);
            var wrongSecret = WebhookSigner.Sign("other-secret-not-real", Body);
            Assert.Equal("KEY001", receiver.CounselMessage(wrongSecret.Headers, wrongSecret.Body).MsgKey);
            var bogus = new Dictionary<string, string> { [Webhooks.TimestampHeader] = "not-a-timestamp", [Webhooks.SignatureHeader] = "zz" };
            Assert.Equal("KEY001", receiver.CounselMessage(bogus, Body).MsgKey);
            var half = new Dictionary<string, string> { [Webhooks.TimestampHeader] = "1" };
            Assert.Equal("KEY001", receiver.CounselMessage(half, Body).MsgKey);
        }

        [Fact]
        public void Counsel_webhooks_keep_input_defenses()
        {
            // SDK-DESIGN §12.7: size cap, JSON depth 64, shape/type checks
            var receiver = new WebhookReceiver(Secret);
            var none = new Dictionary<string, string>();
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.CounselMessage(none, new byte[Webhooks.MaxBodyBytes + 1]));
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseCounselMessage(new byte[Webhooks.MaxBodyBytes + 1]));
            var deep = "{\"msgKey\":\"KEY001\",\"extra\":" + new string('[', 70) + new string(']', 70) + "}";
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.CounselMessage(none, deep));
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseCounselMessage(deep));
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.CounselMessage(none, "[1]"));
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.CounselMessage(none, "not json"));
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.CounselMessage(none, "{\"msgKey\":1}"));
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseCounselMessage("{\"msgKey\":\"KEY001\",\"contents\":\"x\"}"));
            Assert.Throws<ArgumentNullException>(() => receiver.CounselMessage((IEnumerable<KeyValuePair<string, string>>)null!, Body));
        }

        [Fact]
        public void Report_and_mo_webhooks_still_require_a_signature()
        {
            var receiver = new WebhookReceiver(Secret);
            var plain = WebhookSigner.WithoutSignature("{\"msgKey\":\"KEY001\"}");
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Report(plain.Headers, plain.Body));
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Mo(plain.Headers, plain.Body));
            var wrong = WebhookSigner.Sign("other-secret-not-real", "{\"msgKey\":\"KEY001\"}");
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Report(wrong.Headers, wrong.Body));
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Mo(wrong.Headers, wrong.Body));
            var signed = WebhookSigner.Sign(Secret, "{\"msgKey\":\"KEY001\"}");
            Assert.Equal("KEY001", receiver.Report(signed.Headers, signed.Body).MsgKey);
        }

        [Fact]
        public void Counsel_parsing_needs_no_secret()
        {
            // the documented secret-less path: static Webhooks.ParseCounsel* (README)
            Assert.Equal("USER_KEY_EXAMPLE", Webhooks.ParseCounselMessage(Body).UserKey);
            Assert.Equal("KEY001", Webhooks.ParseCounselResult(System.Text.Encoding.UTF8.GetBytes("{\"msgKey\":\"KEY001\"}")).MsgKey);
            Assert.Equal("WebhookReceiver(Tolerance=300s)", new WebhookReceiver(Secret).ToString());
            Assert.Equal("WebhookReceiverOptions(Tolerance=300s)", new WebhookReceiverOptions().ToString());
        }

        [Fact]
        public void Counsel_ack_is_code_and_result()
        {
            Assert.Equal("{\"code\":\"A000\",\"result\":\"Success\"}", Webhooks.CounselAckJson());
            Assert.Equal("A000", WebhookReceiver.CounselAck().Code);
            Assert.Equal("{\"msgKey\":\"KEY001\"}", Webhooks.AckJson("KEY001"));
        }

        [Fact]
        public void Counsel_payloads_mask_free_text_in_ToString()
        {
            var payload = Webhooks.ParseCounselMessage("{\"msgKey\":\"KEY001\",\"content\":\"개인 상담 내용\"}");
            Assert.DoesNotContain("상담 내용", payload.ToString(), StringComparison.Ordinal);
            Assert.Contains("content=<8 chars>", payload.ToString(), StringComparison.Ordinal);
        }
    }
}
