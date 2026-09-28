using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Bizgo.Models;

namespace Bizgo.Examples
{
    /// <summary>Reads example settings from environment variables (never hard-code keys or phone numbers).</summary>
    public static class Env
    {
        /// <summary>Returns the variable or stops with a hint.</summary>
        public static string Require(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException("환경변수 " + name + "를 설정하세요 (examples/README.md 참고)");
            }

            return value;
        }
    }

    /// <summary>Send an SMS and check the per-recipient acceptance result.</summary>
    public static class SendSms
    {
        public static async Task RunAsync(BizgoClient client, string from, string to)
        {
            var result = await client.Send.SmsAsync(to, from, "[비즈고] 인증번호는 123456 입니다.", new SendOptions { Ref = "signup-otp" });

            foreach (var rejected in result.Failed)
            {
                // accepted != delivered; rejected recipients never get the message
                Console.WriteLine("접수 실패: " + rejected.Code + " " + rejected.Result);
            }

            Console.WriteLine("접수된 메시지 키: " + string.Join(", ", result.MsgKeys)); // final delivery result comes later as a report
        }
    }

    /// <summary>Send a Kakao AlimTalk message; if it fails, Bizgo sends the SMS instead (fallback).</summary>
    public static class SendAlimtalkFallback
    {
        public static async Task RunAsync(BizgoClient client, string from, string to, string senderKey, string templateCode)
        {
            const string OrderId = "20260923-0001";
            try
            {
                var result = await client.Send.OmniAsync(
                    new Destination(to) { ReplaceWords = new Dictionary<string, string> { ["name"] = "홍길동", ["order"] = OrderId } },
                    new IChannelMessage[]
                    {
                        new AlimtalkMessage
                        {
                            SenderKey = senderKey,
                            TemplateCode = templateCode,
                            MsgType = "AT",
                            Text = "#{name}님, 주문(#{order})이 접수되었습니다.", // must match the approved template
                        },
                        new SmsMessage { From = from, Text = "#{name}님, 주문(#{order})이 접수되었습니다." },
                    },
                    new SendOptions
                    {
                        // same key => Bizgo rejects a second send, so retries after a timeout are safe
                        IdempotencyKey = "order-" + OrderId,
                        Ref = OrderId,
                    });
                Console.WriteLine("접수: " + string.Join(", ", result.MsgKeys) + " 실패: " + string.Join(", ", result.Failed.Select(d => d.Code)));
            }
            catch (BizgoDuplicateRequestException)
            {
                Console.WriteLine("이미 발송된 주문입니다.");
            }
        }
    }

    /// <summary>Upload an image, then send an MMS with it.</summary>
    public static class SendMms
    {
        public static async Task RunAsync(BizgoClient client, string from, string to, byte[] image)
        {
            var uploaded = await client.Files.UploadMmsAsync(FileUpload.FromBytes(image, "banner.jpg")); // jpg, max 300KB
            if (string.IsNullOrEmpty(uploaded.FileKey))
            {
                throw new InvalidOperationException("업로드 응답에 fileKey가 없습니다");
            }

            var result = await client.Send.MmsAsync(to, from, "첨부 이미지를 확인해 주세요.", new[] { uploaded.FileKey! }, title: "이벤트 안내");
            Console.WriteLine("접수: " + string.Join(", ", result.MsgKeys) + " 파일 키 만료: " + uploaded.Expired);
        }
    }

    /// <summary>Process delivery reports with polling (the API key must be set to POLLING in the console).</summary>
    public static class PollReports
    {
        /// <summary>Stand-in for your database.</summary>
        public static ConcurrentDictionary<string, string> Store { get; } = new ConcurrentDictionary<string, string>();

        public static async Task RunAsync(BizgoClient client)
        {
            // polls, calls the handler, acks only after the handler succeeded
            var handled = await client.Reports.ConsumeAsync(reports =>
            {
                foreach (var report in reports)
                {
                    // upsert by MsgKey: a batch is delivered again if the handler throws before the ack
                    Store[report.MsgKey ?? string.Empty] = report.ReportCode == "10000" ? "delivered" : "failed:" + report.ReportCode;
                }

                return Task.CompletedTask;
            });
            Console.WriteLine("리포트 " + handled + "건 처리");
        }
    }

    /// <summary>Walk the send history of the last hour, then look up one message's status.</summary>
    public static class MessageHistory
    {
        public static async Task RunAsync(BizgoClient client)
        {
            var since = DateTimeOffset.UtcNow.AddHours(-1); // converted to KST automatically
            var failed = new List<MessageStatus>();
            await foreach (var message in client.Messages.EnumerateHistoryAsync(since, new[] { "SMS", "ALIMTALK" }))
            {
                if (message.ReportCode != "10000")
                {
                    failed.Add(message);
                }
            }

            Console.WriteLine("최근 1시간 실패 " + failed.Count + "건");
            foreach (var message in failed.Take(1))
            {
                foreach (var step in await client.Messages.StatusAsync(message.MsgKey ?? string.Empty)) // one entry per channel tried (fallback)
                {
                    Console.WriteLine(step.ServiceType + " " + step.ReportCode + " " + step.ReportText);
                }
            }
        }
    }

    /// <summary>Send to several recipient groups concurrently with one shared client.</summary>
    public static class ConcurrentSend
    {
        public static async Task RunAsync(BizgoClient client, string from, IEnumerable<string[]> groups)
        {
            var message = new SmsMessage { From = from, Text = "[비즈고] 점검 안내: 오늘 23시부터 1시간" };

            // each request takes up to 200 recipients; the client paces sends at 200 messages (recipients) per second by default
            var results = await Task.WhenAll(groups.Select(group => client.Send.OmniAsync(group, new IChannelMessage[] { message })));
            Console.WriteLine("접수: " + results.Sum(r => r.Succeeded.Count) + " 실패: " + results.Sum(r => r.Failed.Count));
        }
    }

    /// <summary>Send one message to a large list: chunks of 200, 4 requests at a time, deterministic idempotency keys.</summary>
    public static class BulkSend
    {
        public static async Task RunAsync(BizgoClient client, string from, IReadOnlyList<string> recipients, string campaign)
        {
            var message = new SmsMessage { From = from, Text = "[비즈고] 점검 안내: 오늘 23시부터 1시간" };
            var options = new BulkSendOptions { IdempotencyKeyPrefix = campaign }; // re-run with the same list and chunk size: no duplicates
            var result = await client.Send.BulkAsync(recipients.ToArray(), new IChannelMessage[] { message }, options);

            Console.WriteLine("접수: " + result.Succeeded.Count + " 거부: " + result.Failed.Count);
            foreach (var error in result.Errors)
            {
                // index range only, never the numbers; resend these chunks (same list, same chunk size) after checking the cause
                Console.WriteLine("청크 " + error.ChunkIndex + " (" + error.Start + "번부터 " + error.Count + "명) 실패: " + error.Error.GetType().Name);
            }
        }
    }

    /// <summary>A generated resource: list AlimTalk templates page by page, then fetch one.</summary>
    public static class AlimtalkTemplates
    {
        public static async Task RunAsync(BizgoClient client, string senderKey)
        {
            var options = new AlimtalkTemplatesListOptions { InspectionStatus = "APR", Limit = 100 };
            await foreach (var template in client.Alimtalk.Templates.EnumerateAsync(senderKey, options))
            {
                Console.WriteLine(template.TemplateCode + " " + template.TemplateName);
            }

            var first = await client.Alimtalk.Templates.ListAsync(senderKey, new AlimtalkTemplatesListOptions { Limit = 1 });
            if (first.Count > 0 && first[0].TemplateCode is string code)
            {
                var detail = await client.Alimtalk.Templates.GetAsync(senderKey, code); // never null
                Console.WriteLine(detail.TemplateCode + " 검수 상태: " + detail.InspectionStatus);
            }
        }
    }

    /// <summary>
    /// A counsel (상담톡) webhook handler. Counsel webhooks carry no signature (signatures apply only to report and MO webhooks), so no
    /// webhook secret is needed: parse the body with <c>Webhooks.ParseCounsel*</c>. Serve it over HTTPS, allow only the Bizgo webhook
    /// source IPs, and deduplicate by MsgKey. Counsel webhooks are answered with {"code":"A000","result":"Success"}.
    /// </summary>
    public static class CounselWebhook
    {
        public static (int Status, string Body) Handle(byte[] body)
        {
            try
            {
                var message = Webhooks.ParseCounselMessage(body);
                // enqueue by message.SessionId; the body holds personal data: do not log it
                _ = message.SessionId;
            }
            catch (BizgoWebhookVerificationException)
            {
                return (400, "{}");
            }

            return (200, Webhooks.CounselAckJson());
        }
    }

    /// <summary>
    /// Receive delivery report webhooks with <see cref="HttpListener"/>. Production checklist:
    /// serve over HTTPS and allow only the Bizgo webhook source IPs; answer within 5 seconds (do slow work asynchronously);
    /// deduplicate by MsgKey (Bizgo retries up to 3 times).
    /// </summary>
    public static class WebhookServer
    {
        private static readonly ConcurrentDictionary<string, bool> Seen = new ConcurrentDictionary<string, bool>(); // stand-in for a persistent dedup store

        /// <summary>Returns (HTTP status, JSON body). Framework-independent so it is easy to test and reuse.</summary>
        public static (int Status, string Body) Handle(WebhookReceiver receiver, IEnumerable<KeyValuePair<string, string>> headers, byte[] body)
        {
            ReportWebhookPayload report;
            try
            {
                report = receiver.Report(headers, body);
            }
            catch (BizgoWebhookVerificationException)
            {
                return (401, "{}");
            }

            if (Seen.TryAdd(report.MsgKey ?? string.Empty, true))
            {
                // enqueue the report for processing here
            }

            return (200, Webhooks.AckJson(report.MsgKey ?? string.Empty));
        }

        public static async Task RunAsync(string secret, string prefix)
        {
            var receiver = new WebhookReceiver(secret);
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix); // put a TLS reverse proxy in front
            listener.Start();
            while (true)
            {
                var context = await listener.GetContextAsync();
                using var buffer = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(buffer);
                var headers = context.Request.Headers.AllKeys
                    .Where(k => k is not null)
                    .Select(k => new KeyValuePair<string, string>(k!, context.Request.Headers[k] ?? string.Empty));
                var (status, json) = buffer.Length > Webhooks.MaxBodyBytes ? (413, "{}") : Handle(receiver, headers, buffer.ToArray());
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close(); // no access log: request lines and bodies can contain phone numbers
            }
        }
    }
}
