using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>
    /// Bizgo rejects <c>idempotencyKey</c> without <c>idempotencyTtl</c> (A309, sandbox 2026-09-28): a key without a TTL is sent with
    /// <see cref="BizgoClient.DefaultIdempotencyTtl"/>; an explicit TTL (0 included) is kept; no key, no TTL.
    /// </summary>
    public class IdempotencyTtlTests
    {
        private const string SendPath = "/api/comm/v1/send/omni";

        [Fact]
        public void Default_is_the_maximum_ttl()
        {
            Assert.Equal(86400, BizgoClient.DefaultIdempotencyTtl);
        }

        [Fact]
        public async Task Key_without_ttl_sends_the_default()
        {
            var body = await SendAsync(new SendOptions { IdempotencyKey = "order-1" });

            Assert.Equal("order-1", (string?)body["idempotencyKey"]);
            Assert.Equal(BizgoClient.DefaultIdempotencyTtl, (int?)body["idempotencyTtl"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(600)]
        [InlineData(86400)]
        public async Task Explicit_ttl_is_kept(int ttl)
        {
            var body = await SendAsync(new SendOptions { IdempotencyKey = "order-1", IdempotencyTtl = ttl });

            Assert.Equal(ttl, (int?)body["idempotencyTtl"]);
        }

        [Fact]
        public async Task No_key_adds_no_ttl()
        {
            var body = await SendAsync(new SendOptions { Ref = "r" });

            Assert.False(body.ContainsKey("idempotencyKey"));
            Assert.False(body.ContainsKey("idempotencyTtl"));
        }

        [Fact]
        public async Task Ttl_without_key_is_sent_as_is()
        {
            var body = await SendAsync(new SendOptions { IdempotencyTtl = 600 });

            Assert.False(body.ContainsKey("idempotencyKey"));
            Assert.Equal(600, (int?)body["idempotencyTtl"]);
        }

        [Fact]
        public async Task Prepared_request_gets_the_default_without_being_changed()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));
            var request = new SendOmniRequest
            {
                Destinations = new List<Destination> { new Destination(Placeholders.Phone) },
                MessageFlow = new List<MessageFlowItem> { MessageFlowItem.Of(new SmsMessage { From = Placeholders.Phone, Text = "x" }) },
                IdempotencyKey = "order-1",
            };
            var before = request.ToJson();

            await t.Client.Send.RequestAsync(request);

            Assert.Equal(BizgoClient.DefaultIdempotencyTtl, (int?)t.Handler.Last.Json["idempotencyTtl"]);
            Assert.Null(request.IdempotencyTtl);
            Assert.Equal(before, request.ToJson());
        }

        [Fact]
        public async Task Bulk_chunks_with_key_prefix_get_the_default()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.BulkAsync(
                new[] { "01000000000", "01000000001", "01000000002" },
                new IChannelMessage[] { new SmsMessage { From = Placeholders.Phone, Text = "x" } },
                new BulkSendOptions { ChunkSize = 2, IdempotencyKeyPrefix = "camp", Concurrency = 1 });

            Assert.Equal(2, t.Handler.Requests.Count);
            Assert.All(t.Handler.Requests, r => Assert.Equal(BizgoClient.DefaultIdempotencyTtl, (int?)r.Json["idempotencyTtl"]));
        }

        [Fact]
        public async Task Bulk_chunks_keep_explicit_ttl_and_add_none_without_prefix()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));
            var messages = new IChannelMessage[] { new SmsMessage { From = Placeholders.Phone, Text = "x" } };

            await t.Client.Send.BulkAsync(new[] { "01000000000", "01000000001" }, messages, new BulkSendOptions { ChunkSize = 1, IdempotencyKeyPrefix = "camp", IdempotencyTtl = 0, Concurrency = 1 });
            await t.Client.Send.BulkAsync(new[] { "01000000000" }, messages, new BulkSendOptions { Concurrency = 1 });

            var requests = t.Handler.Requests;
            Assert.Equal(3, requests.Count);
            Assert.All(requests.Take(2), r => Assert.Equal(0, (int?)r.Json["idempotencyTtl"]));
            Assert.False(requests[2].Json.AsObject().ContainsKey("idempotencyTtl"));
        }

        [Fact]
        public void Json_body_helper_adds_the_default_only_when_absent_and_never_mutates()
        {
            var request = new SendOmniRequest { IdempotencyKey = "k" };

            var withDefault = Body(JsonBody.Create(request, BizgoJsonContext.Default.SendOmniRequest, request.IdempotencyKey, request.IdempotencyTtl));
            Assert.Equal(BizgoClient.DefaultIdempotencyTtl, (int?)withDefault["idempotencyTtl"]);
            Assert.Null(request.IdempotencyTtl);

            request.IdempotencyTtl = 0;
            Assert.Equal(0, (int?)Body(JsonBody.Create(request, BizgoJsonContext.Default.SendOmniRequest, request.IdempotencyKey, request.IdempotencyTtl))["idempotencyTtl"]);

            var noKey = new SendOmniRequest { Ref = "r" };
            Assert.False(Body(JsonBody.Create(noKey, BizgoJsonContext.Default.SendOmniRequest, noKey.IdempotencyKey, noKey.IdempotencyTtl)).ContainsKey("idempotencyTtl"));
        }

        private static async Task<JsonObject> SendAsync(SendOptions options)
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.SmsAsync(Placeholders.Phone, Placeholders.Phone, "x", options);

            return t.Handler.Last.Json.AsObject();
        }

        private static JsonObject Body(Func<HttpContent> content)
        {
            using var c = content();
            var text = c.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonNode.Parse(text)!.AsObject();
        }
    }
}
