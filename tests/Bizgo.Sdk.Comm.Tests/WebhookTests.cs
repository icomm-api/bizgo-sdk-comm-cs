using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Bizgo.Tests
{
    public class WebhookTests
    {
        private const string Secret = "test-webhook-secret";
        private const string Timestamp = "1743381600000"; // epoch ms, as in the API reference example
        private const string Body = "{\"msgKey\":\"KEY001\",\"serviceType\":\"SMS\",\"reportTime\":\"t\",\"reportType\":\"0\",\"reportCode\":\"10000\"}";
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1743381600);

        public static IEnumerable<object[]> Encodings()
        {
            var digest = Sign();
            var hex = Hex(digest);
            yield return new object[] { hex };
            yield return new object[] { hex.ToUpperInvariant() };
            yield return new object[] { Convert.ToBase64String(digest) };
            yield return new object[] { " " + hex + " " };
        }

        [Theory]
        [MemberData(nameof(Encodings))]
        public void Valid_signature_encodings(string signature)
        {
            Webhooks.VerifySignature(Secret, Timestamp, signature, now: Now);
        }

        [Theory]
        [InlineData("sha256=")]
        [InlineData("SHA256=")]
        public void Prefixed_signature_is_rejected(string prefix)
        {
            var error = Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature(Secret, Timestamp, prefix + Hex(Sign()), now: Now));
            Assert.Contains("일치하지", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Wrong_secret()
        {
            var error = Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature(Secret, Timestamp, Hex(Sign(secret: "other")), now: Now));
            Assert.Contains("일치하지", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Old_or_future_timestamp_is_rejected_to_limit_replay()
        {
            var error = Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature(Secret, Timestamp, Hex(Sign()), now: Now.AddSeconds(301)));
            Assert.Contains("허용 범위", error.Message, StringComparison.Ordinal);
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature(Secret, Timestamp, Hex(Sign()), now: Now.AddSeconds(-301)));
            Webhooks.VerifySignature(Secret, Timestamp, Hex(Sign()), now: Now.AddSeconds(299));
            Webhooks.VerifySignature(Secret, Timestamp, Hex(Sign()), tolerance: TimeSpan.FromHours(1), now: Now.AddMinutes(30));
        }

        [Fact]
        public void Seconds_timestamp_is_accepted()
        {
            Webhooks.VerifySignature(Secret, "1743381600", Hex(Sign("1743381600")), now: Now);
        }

        [Theory]
        [InlineData("", "x")]
        [InlineData(null, "x")]
        [InlineData("abc", "x")]
        [InlineData("-1743381600", "x")]
        [InlineData("99999999999999999999", "x")]
        [InlineData(Timestamp, "")]
        [InlineData(Timestamp, null)]
        [InlineData(Timestamp, "not-a-signature")]
        public void Missing_or_malformed_headers(string? timestamp, string? signature)
        {
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.VerifySignature(Secret, timestamp, signature, now: Now));
        }

        [Fact]
        public void Empty_secret_is_rejected()
        {
            Assert.Throws<BizgoConfigurationException>(() => Webhooks.VerifySignature(string.Empty, Timestamp, Hex(Sign()), now: Now));
            Assert.Throws<BizgoConfigurationException>(() => new WebhookReceiver(string.Empty));
        }

        [Fact]
        public void Receiver_verifies_then_parses()
        {
            var receiver = new WebhookReceiver(Secret, null, () => Now);
            var headers = new Dictionary<string, string> { ["x-ib-timestamp"] = Timestamp, ["X-IB-SIGNATURE"] = Hex(Sign()) }; // case-insensitive names

            var report = receiver.Report(headers, Encoding.UTF8.GetBytes(Body));

            Assert.Equal("KEY001", report.MsgKey);
            Assert.Equal("KEY001", WebhookReceiver.Ack(report.MsgKey!).MsgKey);
            Assert.Equal("{\"msgKey\":\"KEY001\"}", Webhooks.AckJson("KEY001"));
            Assert.DoesNotContain(Secret, receiver.ToString(), StringComparison.Ordinal);

            // header lookup delegate (ASP.NET Core style)
            var viaLookup = receiver.Report(name => name == Webhooks.TimestampHeader ? Timestamp : name == Webhooks.SignatureHeader ? Hex(Sign()) : null, Encoding.UTF8.GetBytes(Body));
            Assert.Equal("10000", viaLookup.ReportCode);
        }

        [Fact]
        public void Receiver_rejects_unsigned_request()
        {
            var receiver = new WebhookReceiver(Secret, null, () => Now);
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Report(new Dictionary<string, string>(), Body));
            Assert.Throws<BizgoWebhookVerificationException>(() => receiver.Report(new Dictionary<string, string> { ["X-IB-Timestamp"] = Timestamp, ["X-IB-Signature"] = new string('0', 64) }, Body));
        }

        [Fact]
        public void Parse_mo_and_invalid_json()
        {
            var mo = Webhooks.ParseMo("{\"msgKey\":\"K\",\"serviceType\":\"MO\",\"msgType\":\"SM\",\"to\":\"#000000\",\"from\":\"01000000000\",\"carrier\":\"10001\",\"originator\":\"01000000000\",\"content\":\"투표 1\",\"occurredTime\":\"t\",\"extra\":1}");
            Assert.Equal(Placeholders.Phone, mo.From);
            Assert.Equal("투표 1", mo.Content);
            Assert.True(mo.AdditionalProperties!.ContainsKey("extra"));

            var error = Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseMo(Encoding.UTF8.GetBytes("not json")));
            Assert.Contains("JSON", error.Message, StringComparison.Ordinal);
            Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseReport("[1,2]"));
        }

        [Fact]
        public void Oversized_body_is_rejected()
        {
            var big = new byte[Webhooks.MaxBodyBytes + 1];
            var error = Assert.Throws<BizgoWebhookVerificationException>(() => Webhooks.ParseReport(big));
            Assert.Contains("너무 큽니다", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Mo_receiver()
        {
            var receiver = new WebhookReceiver(Secret, null, () => Now);
            var headers = new Dictionary<string, string> { ["X-IB-Timestamp"] = Timestamp, ["X-IB-Signature"] = Hex(Sign()) };
            var mo = receiver.Mo(headers, "{\"msgKey\":\"K\",\"serviceType\":\"MO\",\"msgType\":\"SM\",\"content\":\"1\"}");
            Assert.Equal("K", mo.MsgKey);
        }

        private static byte[] Sign(string timestamp = Timestamp, string secret = Secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return hmac.ComputeHash(Encoding.ASCII.GetBytes(timestamp));
        }

        private static string Hex(byte[] digest)
        {
            var sb = new StringBuilder();
            foreach (var b in digest)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
