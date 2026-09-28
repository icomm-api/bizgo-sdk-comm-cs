using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>
    /// Every operation of the vendored spec is reachable from the client with the documented HTTP method, path, retry policy, rate bucket
    /// and pagination (SDK-DESIGN §10). Each case calls the real SDK method with minimal valid arguments built from the spec.
    /// </summary>
    public class OperationCoverageTests
    {
        private const string PathValue = "VALUE_1";

        public static IEnumerable<object[]> Operations() => Spec.Operations.Select(o => new object[] { o.Id });

        public static IEnumerable<object[]> PaginatedOperations() => Spec.Operations.Where(o => o.Pagination is not null).Select(o => new object[] { o.Id });

        [Fact]
        public void Operation_table_matches_the_spec()
        {
            Assert.Equal(146, Spec.Operations.Count);
            Assert.Equal(Spec.Operations.Select(o => o.Id), BizgoOperation.All.Select(o => o.Id));
            foreach (var op in Spec.Operations)
            {
                var sdk = BizgoOperation.Find(op.Id)!;
                Assert.Equal(op.Method, sdk.HttpMethod);
                Assert.Equal(op.Path, sdk.PathTemplate);
                Assert.Equal(op.Resource + "." + op.SdkMethod, sdk.Name);
                Assert.Equal(op.Safe ? BizgoRetryPolicy.Safe : BizgoRetryPolicy.RateLimitOnly, sdk.Retry);
                Assert.Equal(op.SendBucket ? BizgoRateBucket.Send : BizgoRateBucket.Other, sdk.RateBucket);
                Assert.Equal(op.Pagination?["style"]?.GetValue<string>(), sdk.Pagination);
                Assert.Same(sdk, BizgoOperation.Match(op.Method, Regex.Replace(op.Path, @"\{[^}]+\}", PathValue)));
            }

            // exactly the operations marked x-sdk-rate: send use the send bucket (SDK-DESIGN §11.5)
            Assert.Equal(
                new[] { "sendOmni", "createReservation", "addReservationRecipients", "createBrandMessageGroupSend", "sendCounselPlain", "sendCounselRich" }.OrderBy(x => x, StringComparer.Ordinal),
                BizgoOperation.All.Where(o => o.RateBucket == BizgoRateBucket.Send).Select(o => o.Id).OrderBy(x => x, StringComparer.Ordinal));
        }

        [Theory]
        [MemberData(nameof(Operations))]
        public async Task Every_spec_operation_is_reachable(string operationId)
        {
            var op = Spec.Operations.Single(o => o.Id == operationId);
            var sdk = BizgoOperation.Find(operationId)!;
            var methods = Members.Resolve(sdk.Member);
            Assert.NotEmpty(methods);

            foreach (var content in op.Bodies.Count == 0 ? new List<(string Media, string Schema)> { (string.Empty, string.Empty) } : op.Bodies)
            {
                var method = content.Schema.Length == 0 || sdk.HandWritten ? methods.Single() : methods.Single(m => m.Method.GetParameters().Any(p => p.ParameterType.Name == content.Schema));

                // success: one request with the documented method, path and body type
                using (var ok = new CatchAll(_ => Envelope("{}")))
                {
                    using var client = ok.Client();
                    await Members.InvokeAsync(client, method, op);
                    var request = Assert.Single(ok.Requests);
                    Assert.Equal(op.Method, request.Method);
                    Assert.Matches("^" + Regex.Replace(Regex.Escape(op.Path), @"\\\{[^}]+}", Regex.Escape(PathValue)) + "$", request.Path);
                    foreach (var q in op.Params.Where(p => p.In == "query" && p.Required))
                    {
                        Assert.Contains(q.Name + "=", request.Query, StringComparison.Ordinal);
                    }

                    foreach (var h in op.Params.Where(p => p.In == "header" && p.Required))
                    {
                        Assert.Contains(h.Name, request.HeaderNames);
                    }

                    if (content.Media.Length > 0)
                    {
                        Assert.StartsWith(content.Media, request.ContentType ?? string.Empty, StringComparison.Ordinal);
                    }
                    else
                    {
                        Assert.Null(request.ContentType);
                    }
                }

                // retry policy: 503 is retried only for x-sdk-retry: safe (MaxRetries = 2 -> 3 attempts)
                using (var failing = new CatchAll(_ => Envelope(null, HttpStatusCode.ServiceUnavailable)))
                {
                    using var client = failing.Client();
                    await Assert.ThrowsAsync<BizgoInternalServerException>(() => Members.InvokeAsync(client, method, op));
                    Assert.Equal(op.Safe ? 3 : 1, failing.Requests.Count);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PaginatedOperations))]
        public async Task Paginated_operations_walk_every_page(string operationId)
        {
            var op = Spec.Operations.Single(o => o.Id == operationId);
            var sdk = BizgoOperation.Find(operationId)!;
            Assert.NotNull(sdk.Enumerator);
            var method = Members.Resolve(sdk.Enumerator!).Single();
            var p = op.Pagination!;
            var style = p["style"]!.GetValue<string>();
            var cursorValue = op.Params.Single(x => x.Name == p["request"]!.GetValue<string>()).Type == "integer" ? (JsonNode)JsonValue.Create(5L)! : JsonValue.Create("CURSOR_5")!;

            var first = new JsonObject();
            var item = new JsonObject();
            if (style == "cursor" && p["response"]!.GetValue<string>().Contains("[-1]", StringComparison.Ordinal))
            {
                // the cursor is a field of the last item
                var field = p["response"]!.GetValue<string>().Split('.').Last();
                item[field] = cursorValue.DeepClone();
            }

            // a full first page (size = the smallest allowed page size), then an empty one
            var sizeName = style == "cursor" ? null : p["size"]?.GetValue<string>();
            var pageSize = sizeName is null ? 1 : (int)Math.Max(1, op.Params.Single(x => x.Name == sizeName).Schema["minimum"]?.GetValue<long>() ?? 1);
            var items = new JsonArray();
            for (var i = 0; i < pageSize; i++)
            {
                items.Add(item.DeepClone());
            }

            Put(first, p["items"]!.GetValue<string>(), items);
            if (style == "cursor" && !p["response"]!.GetValue<string>().Contains("[-1]", StringComparison.Ordinal))
            {
                Put(first, p["response"]!.GetValue<string>(), cursorValue.DeepClone());
            }

            if (p["hasNext"] is JsonNode hasNext)
            {
                Put(first, hasNext.GetValue<string>(), JsonValue.Create(true));
            }

            if (p["total"] is JsonNode total)
            {
                Put(first, total.GetValue<string>(), JsonValue.Create((pageSize * 10).ToString(CultureInfo.InvariantCulture))); // numbers are also read from strings
            }

            var last = new JsonObject();
            Put(last, p["items"]!.GetValue<string>(), new JsonArray());
            if (p["hasNext"] is JsonNode hasNext2)
            {
                Put(last, hasNext2.GetValue<string>(), JsonValue.Create(false));
            }

            var calls = 0;
            using var fake = new CatchAll(_ => Interlocked.Increment(ref calls) == 1 ? Raw(first) : Raw(last));
            using var client = fake.Client();
            var count = await Members.DrainAsync(client, method, op, sizeName, pageSize);
            Assert.Equal(pageSize, count);
            Assert.Equal(2, fake.Requests.Count);
            var second = fake.Requests[1].Query;
            var request = p["request"]!.GetValue<string>();
            var expected = style == "cursor" ? (cursorValue.GetValueKind() == JsonValueKind.Number ? "5" : "CURSOR_5") : style == "page" ? "2" : pageSize.ToString(CultureInfo.InvariantCulture);
            Assert.Contains(request + "=" + expected, second, StringComparison.Ordinal);
        }

        private static void Put(JsonObject root, string path, JsonNode value)
        {
            var parts = path.Split('.');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var name = parts[i].Replace("[-1]", string.Empty, StringComparison.Ordinal);
                node[name] ??= new JsonObject();
                node = node[name]!.AsObject();
            }

            node[parts[^1]] = value;
        }

        private static HttpResponseMessage Raw(JsonObject body)
        {
            body["common"] = new JsonObject { ["authCode"] = "A000", ["authResult"] = "Success" };
            var data = body["data"] as JsonObject ?? new JsonObject();
            data["code"] ??= "A000";
            body["data"] = data;
            return Responses.Json(body.ToJsonString());
        }

        private static HttpResponseMessage Envelope(string? data, HttpStatusCode status = HttpStatusCode.OK)
        {
            if (status != HttpStatusCode.OK)
            {
                return Responses.Json("{\"common\":{\"authCode\":\"A000\"},\"data\":{\"code\":\"E999\",\"result\":\"Failed\"}}", status);
            }

            return Responses.Json(Responses.Envelope(data));
        }

        /// <summary>The operations of the vendored spec (the test reads the spec itself, independent of the generator).</summary>
        internal static class Spec
        {
            private static readonly Lazy<List<SpecOperation>> All = new Lazy<List<SpecOperation>>(Load);

            internal static List<SpecOperation> Operations => All.Value;

            internal static JsonObject Schemas => (JsonObject)Root.Value["components"]!["schemas"]!;

            private static readonly Lazy<JsonObject> Root = new Lazy<JsonObject>(() => Repo.Spec);

            private static List<SpecOperation> Load()
            {
                var parameters = (JsonObject)Root.Value["components"]!["parameters"]!;
                var list = new List<SpecOperation>();
                foreach (var path in Root.Value["paths"]!.AsObject())
                {
                    var shared = path.Value!["parameters"] as JsonArray ?? new JsonArray();
                    foreach (var entry in path.Value!.AsObject().Where(e => e.Key != "parameters"))
                    {
                        var op = entry.Value!.AsObject();
                        var result = new SpecOperation(
                            (string)op["operationId"]!,
                            entry.Key.ToUpperInvariant(),
                            path.Key,
                            (string)op["x-sdk-resource"]!,
                            (string)op["x-sdk-method"]!,
                            (string)op["x-sdk-retry"]! == "safe",
                            (string?)op["x-sdk-rate"] == "send",
                            op["x-sdk-pagination"] as JsonObject);
                        foreach (var p in shared.Concat(op["parameters"] as JsonArray ?? new JsonArray()))
                        {
                            var resolved = p!["$ref"] is JsonNode r ? parameters[((string)r!).Split('/').Last()]! : p;
                            result.Params.Add(new SpecParam((string)resolved["name"]!, (string)resolved["in"]!, resolved["required"]?.GetValue<bool>() == true || (string)resolved["in"]! == "path", (string?)resolved["schema"]?["type"] ?? "string", resolved["schema"] as JsonObject ?? new JsonObject()));
                        }

                        foreach (var media in op["requestBody"]?["content"]?.AsObject() ?? new JsonObject())
                        {
                            result.Bodies.Add((media.Key, ((string)media.Value!["schema"]!["$ref"]!).Split('/').Last()));
                        }

                        list.Add(result);
                    }
                }

                return list;
            }
        }

        internal sealed class SpecOperation
        {
            internal SpecOperation(string id, string method, string path, string resource, string sdkMethod, bool safe, bool sendBucket, JsonObject? pagination)
            {
                Id = id;
                Method = method;
                Path = path;
                Resource = resource;
                SdkMethod = sdkMethod;
                Safe = safe;
                SendBucket = sendBucket;
                Pagination = pagination;
            }

            internal string Id { get; }

            internal string Method { get; }

            internal string Path { get; }

            internal string Resource { get; }

            internal string SdkMethod { get; }

            internal bool Safe { get; }

            internal bool SendBucket { get; }

            internal JsonObject? Pagination { get; }

            internal List<SpecParam> Params { get; } = new List<SpecParam>();

            internal List<(string Media, string Schema)> Bodies { get; } = new List<(string, string)>();
        }

        internal sealed class SpecParam
        {
            internal SpecParam(string name, string @in, bool required, string type, JsonObject schema)
            {
                Name = name;
                In = @in;
                Required = required;
                Type = type;
                Schema = schema;
            }

            internal string Name { get; }

            internal string In { get; }

            internal bool Required { get; }

            internal string Type { get; }

            internal JsonObject Schema { get; }
        }

        /// <summary>Finds SDK members by path and calls them with minimal valid arguments.</summary>
        internal static class Members
        {
            internal static List<(object Target, MethodInfo Method)> Resolve(string member)
            {
                // resolved against a throwaway client just to get the method list; InvokeAsync re-resolves the target
                using var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Environment = BizgoEnvironment.Sandbox, HttpMessageHandler = new CatchAll(_ => Envelope("{}")) });
                var (target, name) = Target(client, member);
                return target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name).Select(m => ((object)member, m)).ToList();
            }

            internal static async Task InvokeAsync(BizgoClient client, (object Member, MethodInfo Method) method, SpecOperation op)
            {
                var (target, _) = Target(client, (string)method.Member);
                var args = method.Method.GetParameters().Select(p => Argument(p, op, null, 1)).ToArray();
                var task = (Task)method.Method.Invoke(target, args)!;
                await task;
            }

            internal static async Task<int> DrainAsync(BizgoClient client, (object Member, MethodInfo Method) method, SpecOperation op, string? size, int pageSize)
            {
                var (target, _) = Target(client, (string)method.Member);
                var args = method.Method.GetParameters().Select(p => Argument(p, op, size, pageSize)).ToArray();
                var sequence = method.Method.Invoke(target, args)!;
                var element = method.Method.ReturnType.GetGenericArguments()[0];
                var drain = typeof(Members).GetMethod(nameof(Drain), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(element);
                return await (Task<int>)drain.Invoke(null, new[] { sequence })!;
            }

            private static async Task<int> Drain<T>(IAsyncEnumerable<T> items)
            {
                var count = 0;
                await foreach (var item in items)
                {
                    Assert.NotNull(item);
                    count++;
                }

                return count;
            }

            private static (object Target, string Method) Target(BizgoClient client, string member)
            {
                var parts = member.Split('.');
                object target = client;
                foreach (var part in parts.Take(parts.Length - 1))
                {
                    target = target.GetType().GetProperty(part)!.GetValue(target)!;
                }

                return (target, parts[^1]);
            }

            private static object? Argument(ParameterInfo p, SpecOperation op, string? size, int pageSize)
            {
                var type = p.ParameterType;
                if (type == typeof(CancellationToken))
                {
                    return CancellationToken.None;
                }

                var spec = op.Params.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (type.Name.EndsWith("Options", StringComparison.Ordinal) && type.Namespace == "Bizgo" && type != typeof(SendOptions))
                {
                    if (size is null)
                    {
                        return null;
                    }

                    var options = Activator.CreateInstance(type)!;
                    type.GetProperty(char.ToUpperInvariant(size[0]) + size.Substring(1))!.SetValue(options, Convert.ChangeType(pageSize, Nullable.GetUnderlyingType(type.GetProperty(char.ToUpperInvariant(size[0]) + size.Substring(1))!.PropertyType)!, CultureInfo.InvariantCulture));
                    return options;
                }

                if (p.HasDefaultValue && spec is null)
                {
                    return p.DefaultValue;
                }

                if (type == typeof(string))
                {
                    return spec is null ? "X1" : spec.In == "path" ? PathValue : Sample.String(spec.Schema);
                }

                if (type == typeof(int) || type == typeof(long))
                {
                    var min = spec?.Schema["minimum"]?.GetValue<long>() ?? 1;
                    return Convert.ChangeType(Math.Max(min, 1), type, CultureInfo.InvariantCulture);
                }

                if (type == typeof(BizgoDate))
                {
                    return (BizgoDate)"20260101";
                }

                if (type == typeof(BizgoDate?))
                {
                    return p.HasDefaultValue ? null : (BizgoDate?)"20260101";
                }

                if (type == typeof(BizgoDateTime))
                {
                    return (BizgoDateTime)new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(9));
                }

                if (type == typeof(IEnumerable<string>))
                {
                    return new[] { "SMS" };
                }

                if (type == typeof(Recipients))
                {
                    return Recipients.Of(Placeholders.Phone);
                }

                if (type == typeof(IEnumerable<IChannelMessage>))
                {
                    return new IChannelMessage[] { new SmsMessage { From = Placeholders.Phone, Text = "hello" } };
                }

                if (type == typeof(FileUpload))
                {
                    return FileUpload.FromBytes(new byte[] { 1, 2, 3 }, "image.jpg");
                }

                if (type == typeof(BrandImageKind))
                {
                    return BrandImageKind.Default;
                }

                if (type.Namespace == "Bizgo.Models")
                {
                    return Sample.Model(type);
                }

                if (p.HasDefaultValue)
                {
                    return p.DefaultValue;
                }

                throw new InvalidOperationException("no sample for " + type + " " + p.Name + " in " + op.Id);
            }
        }

        /// <summary>Minimal valid values built from the spec schemas (required fields only).</summary>
        internal static class Sample
        {
            internal static object Model(Type type)
            {
                var schema = Spec.Schemas[type.Name] as JsonObject ?? throw new InvalidOperationException("no schema " + type.Name);
                if (type.GetProperties().Any(p => p.PropertyType == typeof(FileUpload) || p.PropertyType == typeof(IList<FileUpload>)))
                {
                    return Form(type, schema);
                }

                var json = Node(schema, 0)!.ToJsonString();
                var model = JsonSerializer.Deserialize(json, BizgoJsonContext.Default.GetTypeInfo(type)!)!;
                type.GetMethod("Validate")!.Invoke(model, null);
                return model;
            }

            internal static string String(JsonObject schema)
            {
                if (schema["enum"] is JsonArray values)
                {
                    return values[0]!.ToString();
                }

                switch ((string?)schema["pattern"])
                {
                    case null:
                        break;
                    case "^[0-9]{8}$":
                        return "20260101";
                    case "^[0-9]{11}$":
                        return Placeholders.Phone;
                    case "^[0-9]{4}-[0-9]{2}-[0-9]{2}$":
                        return "2026-01-01";
                    case "^[0-9]+$":
                        return "1";
                    default:
                        return "X1";
                }

                var min = schema["minLength"]?.GetValue<long>() ?? 0;
                return min > 2 ? new string('x', (int)min) : "X1";
            }

            private static object Form(Type type, JsonObject schema)
            {
                var model = Activator.CreateInstance(type)!;
                var required = (schema["required"] as JsonArray)?.Select(r => (string)r!).ToHashSet() ?? new HashSet<string>();
                foreach (var entry in schema["properties"]!.AsObject())
                {
                    if (!required.Contains(entry.Key))
                    {
                        continue;
                    }

                    var property = type.GetProperty(char.ToUpperInvariant(entry.Key[0]) + entry.Key.Substring(1))!;
                    if (property.PropertyType == typeof(FileUpload))
                    {
                        property.SetValue(model, FileUpload.FromBytes(new byte[] { 1, 2, 3 }, "image.jpg"));
                    }
                    else if (property.PropertyType == typeof(string))
                    {
                        property.SetValue(model, String(entry.Value!.AsObject()));
                    }
                    else
                    {
                        // JSON part: built from the property schema, so allOf "required" additions are honoured
                        var json = Node(entry.Value!.AsObject(), 0)!.ToJsonString();
                        property.SetValue(model, JsonSerializer.Deserialize(json, BizgoJsonContext.Default.GetTypeInfo(property.PropertyType)!));
                    }
                }

                type.GetMethod("Validate")!.Invoke(model, null);
                return model;
            }

            private static JsonNode? Node(JsonObject schema, int depth)
            {
                if (depth > 12)
                {
                    return new JsonObject();
                }

                if (schema["$ref"] is JsonNode reference)
                {
                    return Node(Spec.Schemas[((string)reference!).Split('/').Last()]!.AsObject(), depth + 1);
                }

                if (schema["oneOf"] is JsonArray oneOf)
                {
                    return Node(oneOf[0]!.AsObject(), depth + 1);
                }

                if (schema["allOf"] is JsonArray allOf)
                {
                    var merged = new JsonObject();
                    var required = new HashSet<string>();
                    foreach (var part in allOf)
                    {
                        foreach (var r in part!["required"] as JsonArray ?? new JsonArray())
                        {
                            required.Add((string)r!);
                        }

                        if (Node(part.AsObject(), depth + 1) is JsonObject obj)
                        {
                            foreach (var e in obj.ToList())
                            {
                                obj.Remove(e.Key);
                                merged[e.Key] = e.Value;
                            }
                        }
                    }

                    return merged;
                }

                var type = schema["type"] is JsonArray types ? types.Select(t => (string)t!).First(t => t != "null") : (string?)schema["type"] ?? "object";
                switch (type)
                {
                    case "string":
                        return String(schema);
                    case "integer":
                    case "number":
                        var min = schema["minimum"]?.GetValue<long>() ?? 1;
                        var max = schema["maximum"]?.GetValue<long>() ?? long.MaxValue;
                        return Math.Min(Math.Max(min, 1), max);
                    case "boolean":
                        return true;
                    case "array":
                        var array = new JsonArray();
                        var count = Math.Max(schema["minItems"]?.GetValue<long>() ?? 1, 1);
                        for (var i = 0; i < count; i++)
                        {
                            array.Add(Node(schema["items"]!.AsObject(), depth + 1));
                        }

                        return array;
                    default:
                        var result = new JsonObject();
                        var required = (schema["required"] as JsonArray)?.Select(r => (string)r!).ToList() ?? new List<string>();
                        var properties = schema["properties"] as JsonObject ?? new JsonObject();
                        foreach (var name in required)
                        {
                            if (properties[name] is JsonObject property)
                            {
                                result[name] = Node(property, depth + 1);
                            }
                        }

                        // allOf parts that only add "required" name properties of the referenced part
                        foreach (var name in required.Where(n => properties[n] is null))
                        {
                            result[name] = "X1";
                        }

                        return result;
                }
            }
        }

        /// <summary>A handler that answers every request and records method, path, query, content type and header names (never values).</summary>
        internal sealed class CatchAll : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            private readonly ConcurrentQueue<Seen> _requests = new ConcurrentQueue<Seen>();

            internal CatchAll(Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            internal IReadOnlyList<Seen> Requests => _requests.ToArray();

            internal BizgoClient Client()
            {
                var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Environment = BizgoEnvironment.Sandbox, HttpMessageHandler = this, RateLimit = null });
                client.Transport.Delay = (_, _) => Task.CompletedTask;
                return client;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _requests.Enqueue(new Seen(request.Method.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')), request.Content?.Headers.ContentType?.MediaType, request.Headers.Select(h => h.Key).ToList()));
                var response = _respond(request);
                response.RequestMessage ??= request;
                return Task.FromResult(response);
            }
        }

        internal sealed class Seen
        {
            internal Seen(string method, string path, string query, string? contentType, List<string> headerNames)
            {
                Method = method;
                Path = path;
                Query = query;
                ContentType = contentType;
                HeaderNames = headerNames;
            }

            internal string Method { get; }

            internal string Path { get; }

            internal string Query { get; }

            internal string? ContentType { get; }

            internal List<string> HeaderNames { get; }
        }
    }
}
