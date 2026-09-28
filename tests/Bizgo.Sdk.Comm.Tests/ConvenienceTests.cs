using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>SDK-DESIGN §11: bulk send, client-side rate limiting, hooks and tracing.</summary>
    public class ConvenienceTests
    {
        private const string SendPath = "/api/comm/v1/send/omni";

        private static readonly IChannelMessage[] Sms = { new SmsMessage { From = Placeholders.Phone, Text = "hello" } };

        private static string[] Numbers(int count) => Enumerable.Range(0, count).Select(i => "0100000" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        [Fact]
        public async Task Bulk_aggregates_duplicates_and_all_duplicate_chunk_is_not_an_error()
        {
            using var t = new TestClient(o => o.MaxRetries = 0);
            var call = 0;
            t.Handler.On(HttpMethod.Post, SendPath, request =>
            {
                switch (Interlocked.Increment(ref call))
                {
                    case 1:
                        return Responses.Json(Responses.Accepted("A301", "A301"));
                    case 2:
                        return Responses.Json(Responses.Accepted("A000", "A301"));
                    default:
                        return Responses.Json(Responses.Accepted("A306"));
                }
            });

            var result = await t.Client.Send.BulkAsync(Numbers(5), Sms, new BulkSendOptions { ChunkSize = 2, Concurrency = 1, IdempotencyKeyPrefix = "campaign-1" });

            Assert.Empty(result.Errors);
            Assert.Equal(3, result.Duplicates.Count);
            Assert.Single(result.Succeeded);
            Assert.Equal(new[] { "A306" }, result.Failed.Select(d => d.Code).ToArray());
            Assert.False(result.IsOk); // A306 is a rejection; duplicates alone would not be
            Assert.Equal("BulkSendResult(Chunks=3, Errors=0, Succeeded=1, Failed=1, Duplicates=3)", result.ToString());
        }

        private static HttpResponseMessage AcceptAll(HttpRequestMessage request)
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var codes = body["destinations"]!.AsArray().Select(_ => "A000").ToArray();
            return Responses.Json(Responses.Accepted(codes));
        }

        // ------------------------------------------------------------------ bulk

        [Fact]
        public void Bulk_idempotency_key_matches_the_cross_sdk_vector()
        {
            // SDK-DESIGN §12.3: every language produces these keys
            Assert.Equal("camp-2-0-32fe30d2", SendResource.BulkIdempotencyKey("camp", 2, 0, new[] { "01000000000", "01000000001" }));
            Assert.Equal("camp-2-2-370752d8", SendResource.BulkIdempotencyKey("camp", 2, 2, new[] { "01000000002" }));
        }

        [Fact]
        public async Task Bulk_splits_into_chunks_with_deterministic_idempotency_keys()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, AcceptAll);
            var result = await t.Client.Send.BulkAsync(new[] { "01000000000", "01000000001", "01000000002" }, Sms, new BulkSendOptions { ChunkSize = 2, IdempotencyKeyPrefix = "camp", Concurrency = 1 });

            Assert.True(result.IsOk);
            Assert.Equal(2, result.Results.Count);
            Assert.Equal(3, result.Succeeded.Count);
            Assert.Equal(3, result.MsgKeys.Count);
            var keys = t.Handler.Requests.Select(r => (string)r.Json["idempotencyKey"]!).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "camp-2-0-32fe30d2", "camp-2-2-370752d8" }, keys);
            Assert.All(t.Handler.Requests, r => Assert.True(r.Json["destinations"]!.AsArray().Count <= 2));
        }

        [Fact]
        public async Task Bulk_keeps_accepted_chunks_when_others_fail_for_any_reason()
        {
            using var t = new TestClient(o => o.MaxRetries = 0);
            var call = 0;
            t.Handler.On(HttpMethod.Post, SendPath, request =>
            {
                switch (Interlocked.Increment(ref call))
                {
                    case 2:
                        return Responses.Json(Responses.Envelope(code: "A306"));
                    case 3:
                        throw new InvalidOperationException("unexpected failure inside the HTTP stack");
                    case 4:
                        return Responses.Text("<html>oops</html>", HttpStatusCode.OK);
                    default:
                        return AcceptAll(request);
                }
            });

            var result = await t.Client.Send.BulkAsync(Numbers(5), Sms, new BulkSendOptions { ChunkSize = 1, Concurrency = 1 });

            Assert.Equal(5, result.Results.Count);
            Assert.Equal(2, result.Succeeded.Count); // chunks 0 and 4
            Assert.Equal(new[] { 1, 2, 3 }, result.Errors.Select(e => e.ChunkIndex));
            Assert.IsType<BizgoBadRequestException>(result.Errors[0].Error);
            Assert.IsType<InvalidOperationException>(result.Errors[1].Error);
            Assert.IsType<BizgoInvalidResponseException>(result.Errors[2].Error);
            Assert.Equal(3, result.Errors[2].Start);
            Assert.Equal(1, result.Errors[2].Count);
            Assert.False(result.IsOk);
            foreach (var number in Numbers(5))
            {
                Assert.DoesNotContain(number, result.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(number, string.Join(" ", result.Errors.Select(e => e.ToString())), StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Bulk_validates_every_chunk_before_sending()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, AcceptAll);
            var recipients = Numbers(250).Select(n => new Destination(n)).ToList();
            recipients[230].To = null;
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.BulkAsync(recipients, Sms));
            Assert.Contains(error.Errors, e => e.Path == "destinations[230].to");
            Assert.Empty(t.Handler.Requests);

            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.BulkAsync(Numbers(3), Sms, new BulkSendOptions { ChunkSize = 201 }));
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.BulkAsync(Numbers(3), Sms, new BulkSendOptions { Concurrency = 0 }));
            await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.BulkAsync(Numbers(3), Sms, new BulkSendOptions { IdempotencyKeyPrefix = new string('p', 190) }));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Bulk_limits_concurrency()
        {
            var inFlight = 0;
            var peak = 0;
            using var handler = new DelayHandler(async request =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return AcceptAll(request);
            });
            using var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Environment = BizgoEnvironment.Sandbox, HttpMessageHandler = handler, RateLimit = null });
            var result = await client.Send.BulkAsync(Numbers(12), Sms, new BulkSendOptions { ChunkSize = 1, Concurrency = 3 });
            Assert.Equal(12, result.Succeeded.Count);
            Assert.InRange(peak, 1, 3);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        // ------------------------------------------------------------------ rate limiting

        private sealed class FakeClock
        {
            internal TimeSpan Now { get; private set; }

            internal List<TimeSpan> Waits { get; } = new List<TimeSpan>();

            internal Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                Waits.Add(delay);
                Now += delay;
                return Task.CompletedTask;
            }
        }

        private static (TestClient Client, FakeClock Clock) Limited(double send = 200, double other = 5, Action<BizgoClientOptions>? configure = null)
        {
            var t = new TestClient(configure);
            var clock = new FakeClock();
            var limiter = new RateLimiter(new BizgoRateLimit { SendPerSecond = send, OtherPerSecond = other }, () => clock.Now) { Delay = clock.Delay };
            t.Client.Transport.RateLimiter = limiter;
            return (t, clock);
        }

        [Fact]
        public void Rate_limit_is_on_by_default_and_can_be_turned_off()
        {
            using var on = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey });
            Assert.NotNull(on.Transport.RateLimiter);
            Assert.Equal(200, on.Transport.RateLimiter!.Send.Rate);
            Assert.Equal(5, on.Transport.RateLimiter.Other.Rate);
            using var off = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, RateLimit = null });
            Assert.Null(off.Transport.RateLimiter);
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, RateLimit = new BizgoRateLimit { OtherPerSecond = 0 } }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, RateLimit = new BizgoRateLimit { SendPerSecond = double.PositiveInfinity } }));
        }

        [Fact]
        public async Task Other_bucket_starts_full_then_paces_five_per_second()
        {
            var (t, clock) = Limited();
            using (t)
            {
                t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/report/polling", Responses.Envelope("{}"));
                for (var i = 0; i < 7; i++)
                {
                    await t.Client.Reports.PollAsync();
                }

                // burst of 5 without waiting, then 0.2 s per request
                Assert.Equal(new[] { 0.2, 0.2 }, clock.Waits.Select(w => Math.Round(w.TotalSeconds, 3)));
            }
        }

        [Fact]
        public async Task Send_bucket_counts_recipients_not_requests()
        {
            var (t, clock) = Limited();
            using (t)
            {
                t.Handler.On(HttpMethod.Post, SendPath, AcceptAll);
                await t.Client.Send.OmniAsync(Numbers(200), Sms);
                Assert.Empty(clock.Waits);
                await t.Client.Send.OmniAsync(Numbers(100), Sms);
                Assert.Equal(0.5, Math.Round(clock.Waits.Single().TotalSeconds, 3));

                // reads never use the send bucket
                t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/report/polling", Responses.Envelope("{}"));
                await t.Client.Reports.PollAsync();
                Assert.Single(clock.Waits);
            }
        }

        [Fact]
        public async Task A_request_larger_than_the_bucket_waits_for_a_full_bucket_and_leaves_debt()
        {
            var (t, clock) = Limited(send: 100);
            using (t)
            {
                t.Handler.On(HttpMethod.Post, SendPath, AcceptAll);
                await t.Client.Send.OmniAsync(Numbers(200), Sms); // full bucket (100) -> goes now, balance -100
                await t.Client.Send.OmniAsync(Numbers(200), Sms); // needs 100 again: waits 2 s
                Assert.Equal(new[] { 2.0 }, clock.Waits.Select(w => Math.Round(w.TotalSeconds, 3)));
            }
        }

        [Fact]
        public async Task Every_attempt_waits_for_tokens()
        {
            var (t, clock) = Limited(other: 1);
            using (t)
            {
                t.Handler.On(HttpMethod.Get, "/api/comm/v1/report/polling", _ => Responses.Json("{}", (HttpStatusCode)429), Responses.Ok(Responses.Envelope("{}")));
                await t.Client.Reports.PollAsync();
                Assert.Equal(2, t.Handler.Requests.Count);
                Assert.Equal(new[] { 1.0 }, clock.Waits.Select(w => Math.Round(w.TotalSeconds, 3)));
            }
        }

        // ------------------------------------------------------------------ hooks

        [Fact]
        public async Task Hooks_get_the_path_template_and_outcome_but_never_values()
        {
            var events = new ConcurrentQueue<BizgoRequestEvent>();
            using var t = new TestClient(o => o.Hooks = new BizgoHooks { OnRequest = events.Enqueue, OnResponse = events.Enqueue });
            t.Handler.On(HttpMethod.Get, "/api/comm/v1/report/inquiry/" + Placeholders.OtherPhone, _ => Responses.Json("{}", HttpStatusCode.ServiceUnavailable), Responses.Ok(Responses.Envelope("{\"report\":[]}")));
            await t.Client.Reports.InquiryAsync(Placeholders.OtherPhone);

            var list = events.ToArray();
            Assert.Equal(4, list.Length);
            Assert.All(list, e => Assert.Equal("getReportInquiry", e.OperationId));
            Assert.All(list, e => Assert.Equal("/api/comm/v1/report/inquiry/{msgKey}", e.PathTemplate));
            Assert.All(list, e => Assert.Equal("reports.inquiry", e.Operation.Name));
            var ends = list.OfType<BizgoResponseEvent>().ToArray();
            Assert.Equal(503, ends[0].StatusCode);
            Assert.True(ends[0].WillRetry);
            Assert.Equal(1, ends[0].Attempt);
            Assert.Equal(200, ends[1].StatusCode);
            Assert.Equal("A000", ends[1].Code);
            Assert.True(ends[1].Succeeded);
            Assert.Equal(2, ends[1].Attempt);
            Assert.All(list, e => Assert.DoesNotContain(Placeholders.OtherPhone, e.ToString(), StringComparison.Ordinal));
            Assert.All(list, e => Assert.DoesNotContain(Placeholders.ApiKey, e.ToString(), StringComparison.Ordinal));
        }

        [Fact]
        public async Task Hooks_report_failures_including_unreadable_responses()
        {
            var ends = new ConcurrentQueue<BizgoResponseEvent>();
            using var t = new TestClient(o => o.Hooks = new BizgoHooks { OnResponse = ends.Enqueue });
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/report/polling", Responses.Envelope(code: "A020"));
            await Assert.ThrowsAsync<BizgoRateLimitException>(() => t.Client.Reports.PollAsync());
            var failure = ends.Last();
            Assert.Equal("A020", failure.Code);
            Assert.Equal(BizgoErrorLayer.Service, failure.Layer);
            Assert.Equal(nameof(BizgoRateLimitException), failure.ErrorType);

            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/report/polling", "{\"common\":{\"authCode\":\"A000\"},\"data\":{\"code\":\"A000\",\"data\":{\"report\":\"not-a-list\"}}}");
            await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Reports.PollAsync());
            Assert.False(ends.Last().Succeeded); // a parse failure is never reported as success
        }

        [Fact]
        public async Task Throwing_hooks_and_log_sinks_never_change_the_result()
        {
            using var t = new TestClient(o =>
            {
                o.Hooks = new BizgoHooks { OnRequest = _ => throw new InvalidOperationException("hook"), OnResponse = _ => throw new InvalidOperationException("hook") };
                o.Log = _ => throw new InvalidOperationException("log");
            });
            t.Handler.On(HttpMethod.Post, SendPath, AcceptAll);
            var result = await t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "hello");
            Assert.Single(result.MsgKeys);
        }

#if NET8_0_OR_GREATER
        [Fact]
        public async Task Activities_are_emitted_on_the_Bizgo_source()
        {
            var activities = new ConcurrentQueue<System.Diagnostics.Activity>();
            using var listener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = source => source.Name == "Bizgo",
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activities.Enqueue,
            };
            System.Diagnostics.ActivitySource.AddActivityListener(listener);

            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, "/api/comm/v1/report/inquiry/" + Placeholders.OtherPhone, Responses.Envelope("{\"report\":[]}"));
            using var parent = new System.Diagnostics.Activity("test-parent").Start(); // other tests run in parallel: keep only our trace
            await t.Client.Reports.InquiryAsync(Placeholders.OtherPhone);

            var activity = Assert.Single(activities, a => a.TraceId == parent.TraceId && a.GetTagItem("bizgo.operation_id") as string == "getReportInquiry");
            Assert.Equal("bizgo reports.inquiry", activity.DisplayName);
            Assert.Equal("GET", activity.GetTagItem("http.request.method"));
            Assert.Equal("/api/comm/v1/report/inquiry/{msgKey}", activity.GetTagItem("url.template"));
            Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
            Assert.Equal("A000", activity.GetTagItem("bizgo.code"));
            Assert.Equal(0, activity.GetTagItem("bizgo.retry_count"));
            Assert.All(activity.TagObjects, tag => Assert.DoesNotContain(Placeholders.OtherPhone, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
        }
#endif

        private sealed class DelayHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

            internal DelayHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
            {
                _respond = respond;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await _respond(request);
                response.RequestMessage ??= request;
                return response;
            }
        }
    }
}
