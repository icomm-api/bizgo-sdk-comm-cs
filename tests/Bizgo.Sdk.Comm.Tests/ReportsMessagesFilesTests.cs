using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    public class ReportsMessagesFilesTests
    {
        private const string Report = "{\"msgKey\":\"KEY001\",\"serviceType\":\"SMS\",\"msgType\":\"SM\",\"reportCode\":\"10000\",\"reportType\":\"0\"}";
        private const string Polling = "/api/comm/v1/report/polling";
        private const string History = "/api/comm/v1/message/history";
        private const string MoHistory = "/api/comm/v1/message/history/mo";

        [Fact]
        public async Task Consume_acks_each_batch_after_handler()
        {
            using var t = new TestClient();
            t.Handler.On(
                HttpMethod.Get,
                Polling,
                Responses.Ok(Responses.Envelope("{\"reportId\":\"R1\",\"report\":[" + Report + "," + Report + "]}")),
                Responses.Ok(Responses.Envelope("{\"reportId\":\"\",\"report\":null}")));
            t.Handler.OnJson(HttpMethod.Delete, Polling + "/R1", Responses.Envelope());
            var seen = new List<string?>();

            var handled = await t.Client.Reports.ConsumeAsync(reports =>
            {
                seen.AddRange(reports.Select(r => r.MsgKey));
                return Task.CompletedTask;
            });

            Assert.Equal(2, handled);
            Assert.Equal(new[] { "KEY001", "KEY001" }, seen);
            Assert.Equal(1, t.Handler.Count(HttpMethod.Delete, Polling + "/R1"));
        }

        [Fact]
        public async Task Consume_does_not_ack_when_handler_fails()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Polling, Responses.Envelope("{\"reportId\":\"R1\",\"report\":[" + Report + "]}"));
            t.Handler.OnJson(HttpMethod.Delete, Polling + "/R1", Responses.Envelope());

            await Assert.ThrowsAsync<InvalidOperationException>(() => t.Client.Reports.ConsumeAsync((_, _) => throw new InvalidOperationException("db down")));

            Assert.Equal(0, t.Handler.Count(HttpMethod.Delete, Polling + "/R1"));
        }

        [Fact]
        public async Task Consume_respects_max_batches()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Polling, Responses.Envelope("{\"reportId\":\"R1\",\"report\":[" + Report + "]}"));
            t.Handler.OnJson(HttpMethod.Delete, Polling + "/R1", Responses.Envelope());

            Assert.Equal(2, await t.Client.Reports.ConsumeAsync(_ => Task.CompletedTask, maxBatches: 2));
            Assert.Equal(2, t.Handler.Count(HttpMethod.Delete, Polling + "/R1"));
        }

        [Fact]
        public async Task Path_parameters_are_escaped()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/report/inquiry/a%2Fb%3Fc", Responses.Envelope("{\"report\":[" + Report + "]}"));

            var reports = await t.Client.Reports.InquiryAsync("a/b?c");

            Assert.Single(reports);
            Assert.EndsWith("/report/inquiry/a%2Fb%3Fc", t.Handler.Last.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(string.Empty, t.Handler.Last.Uri.Query);
        }

        [Theory]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        public async Task Dot_segments_and_empty_values_are_rejected(string value)
        {
            using var t = new TestClient();
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Reports.AckAsync(value));
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Messages.StatusAsync(value));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Enumerate_history_follows_last_seq()
        {
            using var t = new TestClient();
            const string Msg = "{\"msgKey\":\"K\",\"serviceType\":\"SMS\"}";
            t.Handler.On(
                HttpMethod.Get,
                History,
                Responses.Ok(Responses.Envelope("{\"messages\":[" + Msg + "," + Msg + "],\"lastSeq\":10,\"hasNext\":true}")),
                Responses.Ok(Responses.Envelope("{\"messages\":[" + Msg + "],\"lastSeq\":11,\"hasNext\":false}")));

            var items = new List<MessageStatus>();
            await foreach (var item in t.Client.Messages.EnumerateHistoryAsync(new DateTime(2026, 9, 23, 9, 0, 0), new[] { "SMS", "RCS" }, limit: 2))
            {
                items.Add(item);
            }

            Assert.Equal(3, items.Count);
            var first = t.Handler.Requests[0].Query;
            var second = t.Handler.Requests[1].Query;
            Assert.Equal("2026-09-23T09:00:00", first["requestTime"]);
            Assert.Equal("SMS,RCS", first["serviceType"]);
            Assert.Equal("2", first["limit"]);
            Assert.False(first.ContainsKey("lastSeq"));
            Assert.Equal("10", second["lastSeq"]);
        }

        [Fact]
        public async Task Enumerate_history_stops_if_cursor_does_not_move()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, History, Responses.Envelope("{\"messages\":[{\"msgKey\":\"K\"}],\"lastSeq\":5,\"hasNext\":true}"));

            var count = 0;
            await foreach (var _ in t.Client.Messages.EnumerateHistoryAsync("2026-09-23T09:00:00"))
            {
                count++;
            }

            Assert.Equal(2, count);
            Assert.Equal(2, t.Handler.Count(HttpMethod.Get, History));
        }

        [Fact]
        public async Task Enumerate_history_stops_without_last_seq()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, History, Responses.Envelope("{\"messages\":[{\"msgKey\":\"K\"}],\"hasNext\":true}"));

            var count = 0;
            await foreach (var _ in t.Client.Messages.EnumerateHistoryAsync("2026-09-23T09:00:00"))
            {
                count++;
            }

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task Mo_history_time_is_sent_with_kst_offset()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, MoHistory, Responses.Envelope("{\"messages\":[],\"hasNext\":false}"));

            await t.Client.Messages.MoHistoryAsync(new DateTime(2026, 4, 23, 14, 11, 1), from: Placeholders.Phone);
            Assert.Equal("2026-04-23T14:11:01+09:00", t.Handler.Last.Query["occurredTime"]);
            Assert.Equal(Placeholders.Phone, t.Handler.Last.Query["from"]);
            Assert.Contains("%2B09%3A00", t.Handler.Last.Uri.Query, StringComparison.Ordinal); // '+' is escaped, not a space

            await t.Client.Messages.MoHistoryAsync(new DateTimeOffset(2026, 4, 23, 5, 11, 1, TimeSpan.Zero));
            Assert.Equal("2026-04-23T14:11:01+09:00", t.Handler.Last.Query["occurredTime"]);
        }

        [Fact]
        public async Task Aware_times_are_converted_to_kst()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, History, Responses.Envelope("{\"messages\":[]}"));

            await t.Client.Messages.HistoryAsync(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
            Assert.Equal("2026-09-23T09:00:00", t.Handler.Last.Query["requestTime"]);

            await t.Client.Messages.HistoryAsync(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("2026-09-23T09:00:00", t.Handler.Last.Query["requestTime"]);

            await t.Client.Messages.HistoryAsync(new DateTimeOffset(2026, 9, 23, 18, 30, 0, TimeSpan.FromHours(9)));
            Assert.Equal("2026-09-23T18:30:00", t.Handler.Last.Query["requestTime"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1001)]
        public async Task Limit_range(int limit)
        {
            using var t = new TestClient();
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Messages.HistoryAsync("2026-09-23T09:00:00", limit: limit));
            Assert.Contains("1~1000", error.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Messages.MoHistoryAsync("2026-09-23T09:00:00+09:00", limit: limit));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Statistics()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/message/statistics", Responses.Envelope("{\"statistics\":[{\"statDate\":\"20260923\",\"recvTotalCnt\":3}]}"));

            var stats = await t.Client.Messages.StatisticsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 23), serviceType: "SMS");

            Assert.Equal(3, stats[0].RecvTotalCnt);
            Assert.Equal(
                new Dictionary<string, string> { ["startDate"] = "20260901", ["endDate"] = "20260923", ["serviceType"] = "SMS" },
                t.Handler.Last.Query);

            // an absolute time is converted to the KST date
            await t.Client.Messages.StatisticsAsync(new DateTimeOffset(2026, 9, 30, 20, 0, 0, TimeSpan.Zero));
            Assert.Equal("20261001", t.Handler.Last.Query["startDate"]);
        }

        [Theory]
        [InlineData("2026-09-01")]
        [InlineData("2026091")]
        [InlineData("")]
        public async Task Statistics_date_text_is_checked(string date)
        {
            using var t = new TestClient();
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Messages.StatisticsAsync(date));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Response_keeps_unknown_fields()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/message/inquiry/msgKey/K", Responses.Envelope("{\"messages\":[{\"msgKey\":\"K\",\"newServerField\":\"v\"}]}"));

            var status = (await t.Client.Messages.StatusAsync("K"))[0];

            Assert.NotNull(status.AdditionalProperties);
            Assert.Equal("v", status.AdditionalProperties!["newServerField"].GetString());
        }

        [Fact]
        public async Task Status_by_request_id_and_mo_lookup()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/message/inquiry/requestId/REQ", Responses.Envelope("{\"messages\":[{\"msgKey\":\"REQ001\"},{\"msgKey\":\"REQ002\"}]}"));
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/message/inquiry/mo/msgKey/M1", Responses.Envelope("{\"messages\":[{\"msgKey\":\"M1\",\"from\":\"01000000000\",\"content\":\"1\"}]}"));

            Assert.Equal(2, (await t.Client.Messages.StatusByRequestIdAsync("REQ")).Count);
            var mo = await t.Client.Messages.MoAsync("M1");
            Assert.Equal(Placeholders.Phone, mo.Single().From);
        }

        [Fact]
        public async Task Upload_mms_sends_multipart()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/file/mms", Responses.Envelope("{\"fileKey\":\"FILE_KEY_001\",\"expired\":\"2027-04-24T10:45:24+09:00\"}"));

            var result = await t.Client.Files.UploadMmsAsync(FileUpload.FromBytes(new byte[] { 0xFF, 0xD8, 1, 2 }, "a.jpg"), imageName: "banner");

            var request = t.Handler.Last;
            Assert.StartsWith("multipart/form-data; boundary=", request.ContentType, StringComparison.Ordinal);
            var text = Encoding.UTF8.GetString(request.Body);
            Assert.Contains("name=\"file\"; filename=\"a.jpg\"", text, StringComparison.Ordinal);
            Assert.Contains("Content-Type: image/jpeg", text, StringComparison.Ordinal);
            Assert.Contains("name=\"imageName\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("name=\"fileKey\"", text, StringComparison.Ordinal);
            Assert.Equal("FILE_KEY_001", result.FileKey);
            Assert.Equal("2027-04-24T10:45:24+09:00", result.Expired);
        }

        [Fact]
        public async Task Upload_mms_size_limit()
        {
            using var t = new TestClient();
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Files.UploadMmsAsync(FileUpload.FromBytes(new byte[(300 * 1024) + 1], "a.jpg")));
            Assert.Contains("300KB", error.Message, StringComparison.Ordinal);

            using var stream = new MemoryStream(new byte[(300 * 1024) + 1]);
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Files.UploadMmsAsync(FileUpload.FromStream(stream, "a.jpg")));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Upload_from_path_and_stream()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/file/rcs", Responses.Envelope("{\"media\":\"maapfile://MEDIA_KEY_EXAMPLE\"}"));
            var path = Path.Combine(Path.GetTempPath(), "bizgo-test-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            try
            {
                var result = await t.Client.Files.UploadRcsAsync(FileUpload.FromPath(path), fileKey: "k1");
                Assert.Equal("maapfile://MEDIA_KEY_EXAMPLE", result.Media);
                var text = Encoding.UTF8.GetString(t.Handler.Last.Body);
                Assert.Contains("Content-Type: image/png", text, StringComparison.Ordinal);
                Assert.Contains("name=\"fileKey\"", text, StringComparison.Ordinal);
                Assert.DoesNotContain(Path.GetTempPath(), text, StringComparison.Ordinal); // only the file name is sent
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task Upload_is_retried_only_on_429_with_the_same_bytes()
        {
            using var t = new TestClient();
            t.Handler.On(
                HttpMethod.Post,
                "/api/comm/v1/file/brandmessage/wide",
                _ => Responses.Json("{\"common\":{\"authCode\":\"A020\",\"authResult\":\"Ratelimit\"}}", (HttpStatusCode)429),
                _ => Responses.Text("oops", HttpStatusCode.InternalServerError));

            await Assert.ThrowsAsync<BizgoInternalServerException>(() => t.Client.Files.UploadBrandMessageAsync(FileUpload.FromBytes(new byte[] { 1, 2, 3 }, "w.png"), BrandImageKind.Wide));

            Assert.Equal(2, t.Handler.Requests.Count); // 429 retried, 500 not
            Assert.Equal(t.Handler.Requests[0].Body.Length, t.Handler.Requests[1].Body.Length);
        }

        [Fact]
        public async Task Upload_brand_message_path_and_kind_whitelist()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/file/brandmessage/wideItemList/first", Responses.Envelope("{\"imgUrl\":\"https://example.com/img.jpg\"}"));

            var result = await t.Client.Files.UploadBrandMessageAsync(FileUpload.FromBytes(new byte[] { 1 }, "a.png"), BrandImageKind.WideItemListFirst);
            Assert.Equal("https://example.com/img.jpg", result.ImgUrl);

            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Files.UploadBrandMessageAsync(FileUpload.FromBytes(new byte[] { 1 }, "a.png"), (BrandImageKind)99));
            Assert.Single(t.Handler.Requests);

            var expected = new Dictionary<BrandImageKind, string>
            {
                [BrandImageKind.Default] = "default",
                [BrandImageKind.Wide] = "wide",
                [BrandImageKind.WideItemList] = "wideItemList",
                [BrandImageKind.WideItemListFirst] = "wideItemList/first",
                [BrandImageKind.CarouselFeed] = "carouselFeed",
                [BrandImageKind.CarouselCommerce] = "carouselCommerce",
            };
            foreach (var kind in (BrandImageKind[])Enum.GetValues(typeof(BrandImageKind)))
            {
                Assert.Equal(expected[kind], FilesResource.KindPath(kind));
            }
        }

        [Theory]
        [InlineData("../../etc/passwd", "passwd")]
        [InlineData("C:\\temp\\banner.JPG", "banner.JPG")]
        [InlineData("bad\"name\r\n.png", "badname.png")]
        [InlineData("", "image.jpg")]
        public void Upload_file_names_are_sanitized(string input, string expected)
        {
            Assert.Equal(expected, FileUpload.FromBytes(new byte[] { 1 }, input).FileName);
        }
    }
}
