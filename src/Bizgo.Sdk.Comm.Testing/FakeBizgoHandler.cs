using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Bizgo.Testing
{
    /// <summary>
    /// An in-memory Bizgo server for your tests (SDK-DESIGN §11.3): pass it to a real <see cref="BizgoClient"/> and nothing leaves the process.
    /// </summary>
    /// <remarks>
    /// <para>Every request is recorded (<see cref="Requests"/>) with its operation, method, path, query and JSON body (or multipart field
    /// names and file sizes). Header <b>values</b>, including the API key, are never recorded.</para>
    /// <para>Without a queued response an operation answers with a success envelope: sends (<c>sendOmni</c>, <c>createReservation</c>,
    /// <c>addReservationRecipients</c>) accept every recipient with a fake <c>msgKey</c> (<c>createReservation</c> also returns a fake
    /// <c>resvKey</c>); MMS/RCS/brand message uploads return a fake <c>fileKey</c>/<c>media</c>/<c>imgUrl</c>; everything else returns
    /// <c>data.data = {}</c> (empty lists, no next page). An unknown path answers HTTP 404 (gateway <c>A404</c>).</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using var fake = new FakeBizgoHandler();
    /// using var client = fake.CreateClient();
    /// await client.Send.SmsAsync("01000000000", "01000000000", "hello");   // every recipient A000
    /// Assert.Equal("sendOmni", fake.LastRequest!.OperationId);
    /// fake.On("sendOmni").Fail(BizgoErrorLayer.Service, 200, "A020");        // next call: BizgoRateLimitException
    /// fake.On("sendOmni").SendResult("A000", "A306");                         // then: the second recipient is rejected
    /// </code>
    /// </example>
    public sealed class FakeBizgoHandler : HttpMessageHandler
    {
        /// <summary>The placeholder API key used by <see cref="CreateClient"/>.</summary>
        public const string FakeApiKey = "test-api-key-not-real";

        private readonly ConcurrentQueue<FakeRequest> _requests = new ConcurrentQueue<FakeRequest>();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<FakeRequest, HttpResponseMessage>>> _queued = new ConcurrentDictionary<string, ConcurrentQueue<Func<FakeRequest, HttpResponseMessage>>>(StringComparer.Ordinal);
        private int _sequence;

        /// <summary>Every request so far, in order.</summary>
        public IReadOnlyList<FakeRequest> Requests => _requests.ToArray();

        /// <summary>The most recent request, or null.</summary>
        public FakeRequest? LastRequest => _requests.LastOrDefault();

        /// <summary>
        /// Creates a client wired to this handler: placeholder key, sandbox URL, rate limiting off and no retries (so injected 429/5xx
        /// surface immediately). <paramref name="configure"/> can change any option except the handler.
        /// </summary>
        /// <param name="configure">Changes options before the client is created.</param>
        /// <returns>The client. Dispose it when done.</returns>
        public BizgoClient CreateClient(Action<BizgoClientOptions>? configure = null)
        {
            var options = new BizgoClientOptions
            {
                ApiKey = FakeApiKey,
                Environment = BizgoEnvironment.Sandbox,
                RateLimit = null,
                MaxRetries = 0,
            };
            configure?.Invoke(options);
            options.HttpMessageHandler = this;
            return new BizgoClient(options);
        }

        /// <summary>Queues responses for an operation, by <c>operationId</c> (for example <c>sendOmni</c>).</summary>
        /// <param name="operationId">Operation id.</param>
        /// <returns>A builder; each call on it queues one response, used by the next matching request.</returns>
        /// <exception cref="ArgumentException">The operation is not in the SDK's spec.</exception>
        public FakeResponder On(string operationId)
        {
            var op = BizgoOperation.Find(operationId) ?? throw new ArgumentException("unknown operationId", nameof(operationId));
            return new FakeResponder(this, op);
        }

        /// <summary>Queues responses for an operation, by HTTP method and path template (for example <c>GET</c>, <c>/api/comm/v1/report/inquiry/{msgKey}</c>).</summary>
        /// <param name="httpMethod">HTTP method.</param>
        /// <param name="pathTemplate">Path template as in the spec.</param>
        /// <returns>A builder.</returns>
        /// <exception cref="ArgumentException">No operation has this method and template.</exception>
        public FakeResponder On(string httpMethod, string pathTemplate)
        {
            var op = BizgoOperation.All.FirstOrDefault(o => string.Equals(o.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase) && o.PathTemplate == pathTemplate)
                ?? throw new ArgumentException("unknown operation", nameof(pathTemplate));
            return new FakeResponder(this, op);
        }

        /// <summary>Forgets recorded requests and queued responses.</summary>
        public void Reset()
        {
            while (_requests.TryDequeue(out _))
            {
            }

            _queued.Clear();
        }

        /// <inheritdoc/>
        public override string ToString() => "FakeBizgoHandler(Requests=" + _requests.Count.ToString(CultureInfo.InvariantCulture) + ")";

        internal void Enqueue(BizgoOperation operation, Func<FakeRequest, HttpResponseMessage> response)
        {
            _queued.GetOrAdd(operation.Id, _ => new ConcurrentQueue<Func<FakeRequest, HttpResponseMessage>>()).Enqueue(response);
        }

        internal string NextKey(string prefix) => prefix + Interlocked.Increment(ref _sequence).ToString("D6", CultureInfo.InvariantCulture);

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = await FakeRequest.FromAsync(request).ConfigureAwait(false);
            _requests.Enqueue(recorded);

            HttpResponseMessage response;
            var op = recorded.Operation is null ? null : BizgoOperation.Find(recorded.OperationId!);
            if (op is null)
            {
                response = FakeResponses.Json(FakeResponses.Error(BizgoErrorLayer.Gateway, "A404", "Not Found"), 404);
            }
            else if (_queued.TryGetValue(op.Id, out var queue) && queue.TryDequeue(out var next))
            {
                response = next(recorded);
            }
            else
            {
                response = FakeResponses.Json(FakeResponses.Default(this, op, recorded), 200);
            }

            response.RequestMessage ??= request;
            return response;
        }
    }

    /// <summary>Queues responses for one operation. Each method queues one response and returns the builder, so calls can be chained.</summary>
    public sealed class FakeResponder
    {
        private readonly FakeBizgoHandler _handler;
        private readonly BizgoOperation _operation;

        internal FakeResponder(FakeBizgoHandler handler, BizgoOperation operation)
        {
            _handler = handler;
            _operation = operation;
        }

        /// <summary>Answers with this JSON body and status.</summary>
        /// <param name="json">Response body.</param>
        /// <param name="status">HTTP status.</param>
        /// <param name="retryAfterSeconds">Optional <c>Retry-After</c> header.</param>
        /// <returns>This builder.</returns>
        public FakeResponder Respond(string json, int status = 200, int? retryAfterSeconds = null)
        {
            _handler.Enqueue(_operation, _ => FakeResponses.Json(json, status, retryAfterSeconds));
            return this;
        }

        /// <summary>Answers with a success envelope whose <c>data.data</c> is <paramref name="dataJson"/>.</summary>
        /// <param name="dataJson">JSON of <c>data.data</c>, for example <c>{"report": []}</c>.</param>
        /// <returns>This builder.</returns>
        public FakeResponder RespondData(string dataJson)
        {
            var data = JsonNode.Parse(dataJson);
            _handler.Enqueue(_operation, _ => FakeResponses.Json(FakeResponses.Success(data?.DeepClone()), 200));
            return this;
        }

        /// <summary>Answers with a failure: gateway codes go to <c>common.authCode</c>, service codes to <c>data.code</c>.</summary>
        /// <param name="layer">Layer of the code.</param>
        /// <param name="status">HTTP status (service failures often come with 200).</param>
        /// <param name="code">Code, for example <c>A401</c> (gateway) or <c>A306</c> (service).</param>
        /// <param name="message">Server message.</param>
        /// <param name="retryAfterSeconds">Optional <c>Retry-After</c> header.</param>
        /// <returns>This builder.</returns>
        public FakeResponder Fail(BizgoErrorLayer layer, int status, string code, string message = "Failed", int? retryAfterSeconds = null)
        {
            var body = FakeResponses.Error(layer, code, message);
            _handler.Enqueue(_operation, _ => FakeResponses.Json(body, status, retryAfterSeconds));
            return this;
        }

        /// <summary>Answers HTTP 429 with service code <c>A020</c> (rate limit).</summary>
        /// <param name="retryAfterSeconds">Optional <c>Retry-After</c> header.</param>
        /// <returns>This builder.</returns>
        public FakeResponder RateLimited(int? retryAfterSeconds = null) => Fail(BizgoErrorLayer.Service, 429, "A020", "Too Many Requests", retryAfterSeconds);

        /// <summary>For send operations: answers with one code per recipient (in request order; missing codes are <c>A000</c>).</summary>
        /// <param name="codes">Codes, for example <c>"A000", "A306"</c> (<c>A301</c>: already accepted with the same idempotency key).</param>
        /// <returns>This builder.</returns>
        public FakeResponder SendResult(params string[] codes)
        {
            var copy = (codes ?? Array.Empty<string>()).ToArray();
            _handler.Enqueue(_operation, request => FakeResponses.Json(FakeResponses.Send(_handler, request, copy, _operation.Id == "createReservation"), 200));
            return this;
        }

        /// <summary>
        /// For send operations: answers as Bizgo does for a repeated <c>idempotencyKey</c>: HTTP 200 with every recipient coded <c>A301</c>
        /// (the SDK returns them in <c>SendResult.Duplicates</c>). For a mixed answer use <see cref="SendResult"/>, for example <c>"A000", "A301"</c>.
        /// </summary>
        /// <returns>This builder.</returns>
        public FakeResponder SendDuplicates()
        {
            _handler.Enqueue(_operation, request => FakeResponses.Json(FakeResponses.Send(_handler, request, Enumerable.Repeat("A301", request.Json?["destinations"] is JsonArray a ? a.Count : 0).ToArray(), _operation.Id == "createReservation"), 200));
            return this;
        }

        /// <summary>Makes the next call fail as if the connection broke (the SDK reports <see cref="BizgoConnectionException"/>).</summary>
        /// <returns>This builder.</returns>
        public FakeResponder ConnectionError()
        {
            _handler.Enqueue(_operation, _ => throw new HttpRequestException("fake connection error"));
            return this;
        }

        /// <inheritdoc/>
        public override string ToString() => "FakeResponder(" + _operation.Id + ")";
    }

    /// <summary>A request as the fake server saw it. Header values (and so the API key) are not recorded.</summary>
    public sealed class FakeRequest
    {
        private static readonly char[] Ampersand = { '&' };

        private FakeRequest(string method, string path, string query, BizgoOperation? operation, JsonNode? json, IReadOnlyDictionary<string, FakeFormPart>? form, IReadOnlyList<string> headerNames)
        {
            Method = method;
            Path = path;
            Operation = operation?.Name;
            OperationId = operation?.Id;
            PathTemplate = operation?.PathTemplate;
            QueryString = query;
            Query = ParseQuery(query);
            Json = json;
            Form = form;
            HeaderNames = headerNames;
        }

        /// <summary>Matched <c>operationId</c>, or null for an unknown path.</summary>
        public string? OperationId { get; }

        /// <summary>Matched <c>resource.method</c>, or null.</summary>
        public string? Operation { get; }

        /// <summary>HTTP method.</summary>
        public string Method { get; }

        /// <summary>Real (escaped) path.</summary>
        public string Path { get; }

        /// <summary>Path template of the matched operation.</summary>
        public string? PathTemplate { get; }

        /// <summary>Raw query string without <c>?</c>.</summary>
        public string QueryString { get; }

        /// <summary>Decoded query parameters; a repeated parameter has several values.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; }

        /// <summary>The JSON body, if any.</summary>
        public JsonNode? Json { get; }

        /// <summary>multipart/form-data fields by name, if any.</summary>
        public IReadOnlyDictionary<string, FakeFormPart>? Form { get; }

        /// <summary>Names (not values) of the request headers.</summary>
        public IReadOnlyList<string> HeaderNames { get; }

        /// <summary>Operation and path only (no values from the query or body).</summary>
        /// <returns>For example <c>sendOmni POST /api/comm/v1/send/omni</c>.</returns>
        public override string ToString() => (OperationId ?? "?") + " " + Method + " " + (PathTemplate ?? Path);

        internal static async Task<FakeRequest> FromAsync(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var operation = BizgoOperation.Match(request.Method.Method, uri.AbsolutePath);
            JsonNode? json = null;
            Dictionary<string, FakeFormPart>? form = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                form = new Dictionary<string, FakeFormPart>(StringComparer.Ordinal);
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? string.Empty;
                    var bytes = await part.ReadAsByteArrayAsync().ConfigureAwait(false);
                    var fileName = part.Headers.ContentDisposition?.FileName?.Trim('"') ?? part.Headers.ContentDisposition?.FileNameStar;
                    var contentType = part.Headers.ContentType?.MediaType;
                    var text = fileName is null ? Encoding.UTF8.GetString(bytes) : null;
                    if (form.TryGetValue(name, out var existing))
                    {
                        existing.Add(fileName, contentType, bytes.Length, text);
                    }
                    else
                    {
                        form[name] = new FakeFormPart(name, fileName, contentType, bytes.Length, text);
                    }
                }
            }
            else if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length > 0 && request.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    json = JsonNode.Parse(bytes);
                }
            }

            var headers = request.Headers.Select(h => h.Key).Concat(request.Content?.Headers.Select(h => h.Key) ?? Enumerable.Empty<string>()).ToArray();
            return new FakeRequest(request.Method.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), operation, json, form, headers);
        }

        private static Dictionary<string, IReadOnlyList<string>> ParseQuery(string query)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in query.Split(Ampersand, StringSplitOptions.RemoveEmptyEntries))
            {
                var index = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(index < 0 ? pair : pair.Substring(0, index));
                var value = index < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(index + 1));
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    result[key] = list;
                }

                list.Add(value);
            }

            return result.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>A multipart field: text (and JSON parts) as text, files as name, type and size.</summary>
    public sealed class FakeFormPart
    {
        private readonly List<(string? FileName, string? ContentType, int Size, string? Text)> _parts = new List<(string?, string?, int, string?)>();

        internal FakeFormPart(string name, string? fileName, string? contentType, int size, string? text)
        {
            Name = name;
            Add(fileName, contentType, size, text);
        }

        /// <summary>Field name.</summary>
        public string Name { get; }

        /// <summary>File name of the first part (null for text fields).</summary>
        public string? FileName => _parts[0].FileName;

        /// <summary>Content type of the first part.</summary>
        public string? ContentType => _parts[0].ContentType;

        /// <summary>Size in bytes of the first part.</summary>
        public int Size => _parts[0].Size;

        /// <summary>Text of the first part (text and JSON fields only).</summary>
        public string? Text => _parts[0].Text;

        /// <summary>Number of parts with this name (repeated file fields).</summary>
        public int Count => _parts.Count;

        /// <inheritdoc/>
        public override string ToString() => "FakeFormPart(" + Name + ", Count=" + Count.ToString(CultureInfo.InvariantCulture) + ")";

        internal void Add(string? fileName, string? contentType, int size, string? text) => _parts.Add((fileName, contentType, size, text));
    }

    /// <summary>Envelopes used by the fake server.</summary>
    public static class FakeResponses
    {
        /// <summary><c>{"common": {"authCode": "A000", ...}, "data": {"code": "A000", "result": "Success", "data": ...}}</c>.</summary>
        /// <param name="data">Value of <c>data.data</c>, or null to leave it out.</param>
        /// <returns>JSON text.</returns>
        public static string Success(JsonNode? data = null) => SuccessNode(data).ToJsonString();

        /// <summary>An error envelope: gateway codes set <c>common.authCode</c>, service codes set <c>data.code</c>.</summary>
        /// <param name="layer">Layer.</param>
        /// <param name="code">Code.</param>
        /// <param name="message">Message.</param>
        /// <returns>JSON text.</returns>
        public static string Error(BizgoErrorLayer layer, string code, string message = "Failed")
        {
            var body = new JsonObject();
            if (layer == BizgoErrorLayer.Gateway)
            {
                body["common"] = new JsonObject { ["authCode"] = code, ["authResult"] = message, ["infobankTrId"] = "FAKE-TR-ID" };
            }
            else
            {
                body["common"] = Common();
                body["data"] = new JsonObject { ["code"] = code, ["result"] = message };
            }

            return body.ToJsonString();
        }

        internal static HttpResponseMessage Json(string json, int status, int? retryAfterSeconds = null)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is not null)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds.Value));
            }

            return response;
        }

        internal static string Default(FakeBizgoHandler handler, BizgoOperation op, FakeRequest request)
        {
            switch (op.Id)
            {
                case "sendOmni":
                case "addReservationRecipients":
                    return Send(handler, request, Array.Empty<string>(), reservation: false);
                case "createReservation":
                    return Send(handler, request, Array.Empty<string>(), reservation: true);
                case "uploadMmsFile":
                    return Success(new JsonObject { ["fileKey"] = handler.NextKey("FAKE-FILE-KEY-"), ["expired"] = "2099-12-31T23:59:59+09:00" });
                case "uploadRcsFile":
                    return Success(new JsonObject { ["media"] = handler.NextKey("FAKE-MEDIA-"), ["expired"] = "2099-12-31T23:59:59+09:00" });
                default:
                    if (op.Id.StartsWith("uploadBrandMessage", StringComparison.Ordinal))
                    {
                        return Success(new JsonObject { ["imgUrl"] = "https://example.invalid/" + handler.NextKey("fake-image-") + ".jpg" });
                    }

                    return Success(new JsonObject());
            }
        }

        internal static string Send(FakeBizgoHandler handler, FakeRequest request, IReadOnlyList<string> codes, bool reservation)
        {
            var destinations = new JsonArray();
            var index = 0;
            foreach (var destination in request.Json?["destinations"] as JsonArray ?? new JsonArray())
            {
                var code = index < codes.Count ? codes[index] : "A000";
                destinations.Add((JsonNode)new JsonObject
                {
                    ["to"] = destination?["to"]?.DeepClone(),
                    ["msgKey"] = code == "A000" ? handler.NextKey("FAKE-MSG-KEY-") : null,
                    ["code"] = code,
                    ["result"] = code == "A000" ? "Success" : "Failed",
                });
                index++;
            }

            var result = new JsonObject { ["destinations"] = destinations };
            if (!reservation)
            {
                var body = SuccessNode(result);
                if (request.Json?["ref"] is JsonNode reference)
                {
                    body["data"]!["ref"] = reference.DeepClone();
                }

                return body.ToJsonString();
            }

            var envelope = SuccessNode(result);
            envelope["data"]!["resvKey"] = handler.NextKey("FAKE-RESV-KEY-");
            return envelope.ToJsonString();
        }

        private static JsonObject Common() => new JsonObject { ["authCode"] = "A000", ["authResult"] = "Success", ["infobankTrId"] = "FAKE-TR-ID" };

        private static JsonObject SuccessNode(JsonNode? data)
        {
            var inner = new JsonObject { ["code"] = "A000", ["result"] = "Success" };
            if (data is not null)
            {
                inner["data"] = data;
            }

            return new JsonObject { ["common"] = Common(), ["data"] = inner };
        }
    }
}
