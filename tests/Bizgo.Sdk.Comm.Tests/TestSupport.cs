using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Bizgo.Tests
{
    /// <summary>Placeholder values only (never real keys or phone numbers).</summary>
    internal static class Placeholders
    {
        internal const string ApiKey = "test-api-key-not-real";
        internal const string Phone = "01000000000";
        internal const string OtherPhone = "01000001234";
        internal const string SenderKey = "SENDER_KEY_EXAMPLE";
        internal const string TemplateCode = "TEMPLATE_CODE_EXAMPLE";
    }

    /// <summary>A request as the fake server saw it.</summary>
    internal sealed class RecordedRequest
    {
        internal RecordedRequest(HttpMethod method, Uri uri, Dictionary<string, string> headers, byte[] body, string? contentType)
        {
            Method = method;
            Uri = uri;
            Headers = headers;
            Body = body;
            ContentType = contentType;
        }

        internal HttpMethod Method { get; }

        internal Uri Uri { get; }

        internal Dictionary<string, string> Headers { get; }

        internal byte[] Body { get; }

        internal string? ContentType { get; }

        internal string BodyText => Encoding.UTF8.GetString(Body);

        internal JsonNode Json => JsonNode.Parse(Body)!;

        /// <summary>Query parameters (decoded), in order.</summary>
        internal Dictionary<string, string> Query
        {
            get
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var query = Uri.Query.TrimStart('?');
                if (query.Length == 0)
                {
                    return result;
                }

                foreach (var part in query.Split('&'))
                {
                    var index = part.IndexOf('=');
                    result[Uri.UnescapeDataString(part.Substring(0, index))] = Uri.UnescapeDataString(part.Substring(index + 1));
                }

                return result;
            }
        }
    }

    /// <summary>
    /// In-memory HTTP server: routes by method and path to queued responses (the last one repeats). Nothing leaves the process.
    /// </summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new ConcurrentQueue<RecordedRequest>();
        private readonly List<Route> _routes = new List<Route>();

        internal IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        internal RecordedRequest Last
        {
            get
            {
                var all = Requests;
                return all[all.Count - 1];
            }
        }

        internal FakeHandler On(HttpMethod method, string path, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            lock (_routes)
            {
                _routes.RemoveAll(r => r.Method == method && r.Path == path);
                _routes.Add(new Route(method, path, responses));
            }

            return this;
        }

        internal FakeHandler OnJson(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            return On(method, path, _ => Responses.Json(json, status));
        }

        internal int Count(HttpMethod method, string path) => Requests.Count(r => r.Method == method && r.Uri.AbsolutePath == path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            byte[] body = Array.Empty<byte>();
            string? contentType = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                contentType = request.Content.Headers.ContentType?.ToString();
            }

            _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, body, contentType));

            Route? route;
            lock (_routes)
            {
                route = _routes.FirstOrDefault(r => r.Method == request.Method && r.Path == request.RequestUri!.AbsolutePath);
            }

            if (route is null)
            {
                return Responses.Json("{\"error\":\"no route in test\"}", HttpStatusCode.NotImplemented);
            }

            var response = route.Next()(request);
            response.RequestMessage ??= request;
            return response;
        }

        private sealed class Route
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _responses;
            private int _next;

            internal Route(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            {
                Method = method;
                Path = path;
                _responses = responses;
            }

            internal HttpMethod Method { get; }

            internal string Path { get; }

            internal Func<HttpRequestMessage, HttpResponseMessage> Next()
            {
                var index = Interlocked.Increment(ref _next) - 1;
                return _responses[Math.Min(index, _responses.Length - 1)];
            }
        }
    }

    internal static class Responses
    {
        internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        internal static HttpResponseMessage Text(string text, HttpStatusCode status)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
        }

        internal static Func<HttpRequestMessage, HttpResponseMessage> Ok(string json) => _ => Json(json);

        internal static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception exception) => _ => throw exception;

        /// <summary><c>{common, data}</c> envelope like the real API.</summary>
        internal static string Envelope(string? data = null, string code = "A000", string? @ref = null)
        {
            var body = new JsonObject
            {
                ["common"] = new JsonObject { ["authCode"] = "A000", ["authResult"] = "Success", ["infobankTrId"] = "TR-TEST" },
                ["data"] = new JsonObject { ["code"] = code, ["result"] = code == "A000" ? "Success" : "Failed" },
            };
            if (data is not null)
            {
                body["data"]!["data"] = JsonNode.Parse(data);
            }

            if (@ref is not null)
            {
                body["data"]!["ref"] = @ref;
            }

            return body.ToJsonString();
        }

        internal static string Accepted(params string[] codes)
        {
            var destinations = new JsonArray();
            for (var i = 0; i < codes.Length; i++)
            {
                destinations.Add(new JsonObject { ["to"] = Placeholders.Phone, ["msgKey"] = "KEY" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), ["code"] = codes[i], ["result"] = "r" });
            }

            return Envelope(new JsonObject { ["destinations"] = destinations }.ToJsonString(), @ref: "ref-1");
        }
    }

    /// <summary>A client wired to a <see cref="FakeHandler"/>, with retry delays recorded instead of slept.</summary>
    internal sealed class TestClient : IDisposable
    {
        internal TestClient(Action<BizgoClientOptions>? configure = null)
        {
            Handler = new FakeHandler();
            var options = new BizgoClientOptions
            {
                ApiKey = Placeholders.ApiKey,
                Environment = BizgoEnvironment.Sandbox,
                HttpMessageHandler = Handler,
                Log = line => Log.Enqueue(line),
                RateLimit = null, // rate limiting has its own tests with a fake clock
            };
            configure?.Invoke(options);
            Client = new BizgoClient(options);
            Client.Transport.Delay = (delay, _) =>
            {
                Delays.Enqueue(delay);
                return Task.CompletedTask;
            };
            Client.Transport.Jitter = () => 1.0;
        }

        internal FakeHandler Handler { get; }

        internal BizgoClient Client { get; }

        internal ConcurrentQueue<TimeSpan> Delays { get; } = new ConcurrentQueue<TimeSpan>();

        internal ConcurrentQueue<string> Log { get; } = new ConcurrentQueue<string>();

        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
        }
    }

    internal static class Repo
    {
        internal static string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "spec", "openapi.yaml")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                    {
                        return dir.FullName;
                    }
                }

                throw new InvalidOperationException("repository root not found");
            }
        }

        internal static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()), Encoding.UTF8);

        internal static JsonObject Spec => (JsonObject)Codegen.YamlJson.Parse(Read("spec", "openapi.yaml"))!;
    }

    internal static class JsonAssert
    {
        internal static void Equal(string expected, JsonNode? actual)
        {
            var left = Normalize(JsonNode.Parse(expected));
            var right = Normalize(actual);
            Xunit.Assert.Equal(left, right);
        }

        internal static void Equal(JsonNode? expected, JsonNode? actual)
        {
            Xunit.Assert.Equal(Normalize(expected), Normalize(actual));
        }

        /// <summary>Canonical text with sorted object keys.</summary>
        private static string Normalize(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return "null";
                case JsonObject obj:
                    return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Normalize(p.Value))) + "}";
                case JsonArray array:
                    return "[" + string.Join(",", array.Select(Normalize)) + "]";
                default:
                    return node.ToJsonString();
            }
        }
    }
}
