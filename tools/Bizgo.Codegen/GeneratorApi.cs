using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bizgo.Codegen
{
    /// <summary>
    /// Resource classes, the operation table and webhook parsers, generated from the spec's <c>x-sdk-*</c> metadata
    /// (SDK-DESIGN.md §10, §11.5):
    /// <list type="bullet">
    /// <item><description><c>client.&lt;x-sdk-resource&gt;.&lt;x-sdk-method&gt;Async</c>; dotted resources are nested resource objects.</description></item>
    /// <item><description>Arguments: path parameters (in path order) → request body → required header parameters → required query
    /// parameters → one optional <c>...Options</c> object with the optional query parameters → <c>CancellationToken</c>.</description></item>
    /// <item><description>Return: the model at <c>x-sdk-result</c> (default <c>data.data</c>); never null (rule 19); <c>Task</c> for responses without data.</description></item>
    /// <item><description>Retry from <c>x-sdk-retry</c>; rate bucket <c>send</c> exactly for <c>x-sdk-rate: send</c> (cost = number of destinations, min 1).</description></item>
    /// <item><description><c>x-sdk-pagination</c> adds an <c>Enumerate...Async</c> method returning <c>IAsyncEnumerable&lt;T&gt;</c>.</description></item>
    /// <item><description>Operations implemented by hand-written P0 methods (<see cref="HandWritten"/>) are not generated.</description></item>
    /// </list>
    /// </summary>
    public static partial class Generator
    {
        /// <summary>P0 operations implemented by hand: operationId → member path from the client.</summary>
        private static readonly Dictionary<string, (string Member, string? Enumerator)> HandWritten = new Dictionary<string, (string, string?)>(StringComparer.Ordinal)
        {
            ["sendOmni"] = ("Send.OmniAsync", null),
            ["uploadMmsFile"] = ("Files.UploadMmsAsync", null),
            ["uploadRcsFile"] = ("Files.UploadRcsAsync", null),
            ["getReportPolling"] = ("Reports.PollAsync", null),
            ["ackReportPolling"] = ("Reports.AckAsync", null),
            ["getReportInquiry"] = ("Reports.InquiryAsync", null),
            ["getMessageStatistics"] = ("Messages.StatisticsAsync", null),
            ["getMessageHistory"] = ("Messages.HistoryAsync", "Messages.EnumerateHistoryAsync"),
            ["getMoHistory"] = ("Messages.MoHistoryAsync", "Messages.EnumerateMoHistoryAsync"),
            ["getMessageStatusByMsgKey"] = ("Messages.StatusAsync", null),
            ["getMessageStatusByRequestId"] = ("Messages.StatusByRequestIdAsync", null),
            ["getMoByMsgKey"] = ("Messages.MoAsync", null),
        };

        /// <summary>Resources whose class is hand-written (the generated part is a <c>partial</c> that uses its <c>_transport</c>).</summary>
        private static readonly HashSet<string> HandWrittenResources = new HashSet<string>(StringComparer.Ordinal) { "send", "files", "reports", "messages" };

        /// <summary>Webhooks with hand-written parsers.</summary>
        private static readonly HashSet<string> HandWrittenWebhooks = new HashSet<string>(StringComparer.Ordinal) { "report", "mo" };

        private static readonly HashSet<string> DateFormats = new HashSet<string>(StringComparer.Ordinal) { "YYYYMMDD", "yyyyMMdd" };

        private static readonly HashSet<string> DateTimeFormats = new HashSet<string>(StringComparer.Ordinal)
        {
            "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ssXXX", "yyyy-MM-dd HH:mm:ss", "yyyyMMddHHmmss",
        };

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
            "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
            "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
            "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        };

        private static readonly Regex PathParam = new Regex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // ------------------------------------------------------------------ model

        private sealed class ParamDef
        {
            public ParamDef(string apiName, string location)
            {
                ApiName = apiName;
                Location = location;
                CsName = Identifier(Pascal(apiName));
                PropName = Pascal(apiName);
            }

            public string ApiName { get; }

            /// <summary><c>path</c>, <c>query</c> or <c>header</c>.</summary>
            public string Location { get; }

            public string CsName { get; }

            public string PropName { get; }

            public bool Required { get; set; }

            public string? Description { get; set; }

            public string? Unverified { get; set; }

            /// <summary><c>string</c>, <c>int</c>, <c>long</c>, <c>bool</c>, <c>BizgoDate</c>, <c>BizgoDateTime</c> or <c>IList&lt;string&gt;</c>.</summary>
            public string Type { get; set; } = "string";

            public bool IsValueType => Type != "string" && Type != "IList<string>";

            public string? DateTimeFormat { get; set; }

            public bool Explode { get; set; } = true;

            public long? Minimum { get; set; }

            public long? Maximum { get; set; }

            public long? MinLength { get; set; }

            public long? MaxLength { get; set; }

            public long? MinItems { get; set; }

            public string? Pattern { get; set; }

            public List<string>? Enum { get; set; }

            public List<string>? KnownValues { get; set; }

            public string? Default { get; set; }

            public string? Format { get; set; }
        }

        private sealed class BodyDef
        {
            public BodyDef(string schema, bool form, bool required)
            {
                Schema = schema;
                Form = form;
                Required = required;
            }

            public string Schema { get; }

            public bool Form { get; }

            public bool Required { get; }
        }

        private sealed class PathStep
        {
            public PathStep(string expr, string type, ClassDef? cls, bool list)
            {
                Expr = expr;
                Type = type;
                Class = cls;
                List = list;
            }

            public string Expr { get; }

            /// <summary>Element type for lists, else the value type.</summary>
            public string Type { get; }

            public ClassDef? Class { get; }

            public bool List { get; }
        }

        private sealed class PaginationDef
        {
            public PaginationDef(string style, ParamDef request, PathStep items)
            {
                Style = style;
                Request = request;
                Items = items;
            }

            public string Style { get; }

            public ParamDef Request { get; }

            public ParamDef? Size { get; set; }

            public PathStep Items { get; }

            public PathStep? Cursor { get; set; }

            public PathStep? HasNext { get; set; }

            public PathStep? Total { get; set; }
        }

        private sealed class OpDef
        {
            public OpDef(string id, string httpMethod, string path, string resource, string method)
            {
                Id = id;
                HttpMethod = httpMethod;
                Path = path;
                Resource = resource;
                Method = method;
            }

            public string Id { get; }

            public string HttpMethod { get; }

            public string Path { get; }

            public string Resource { get; }

            public string Method { get; }

            public string Name => Resource + "." + Method;

            public string FieldName => Pascal(Id);

            public string CsMethod => Pascal(Method) + "Async";

            public string? Summary { get; set; }

            public string? Description { get; set; }

            public string? Source { get; set; }

            public bool Safe { get; set; }

            public bool SendBucket { get; set; }

            public List<ParamDef> Params { get; } = new List<ParamDef>();

            public List<BodyDef> Bodies { get; } = new List<BodyDef>();

            public string ResponseClass { get; set; } = "ApiResponse";

            /// <summary>Null: no result (the method returns <c>Task</c>).</summary>
            public PathStep? Result { get; set; }

            public PaginationDef? Pagination { get; set; }

            public bool IsHandWritten => HandWritten.ContainsKey(Id);

            public string? OptionsClass { get; set; }

            public IEnumerable<ParamDef> PathParams => Params.Where(p => p.Location == "path");

            public IEnumerable<ParamDef> HeaderParams => Params.Where(p => p.Location == "header");

            public IEnumerable<ParamDef> RequiredQuery => Params.Where(p => p.Location == "query" && p.Required);

            public IEnumerable<ParamDef> OptionalQuery => Params.Where(p => p.Location == "query" && !p.Required);

            public string EnumeratorName
            {
                get
                {
                    var pascal = Pascal(Method);
                    return (pascal.StartsWith("List", StringComparison.Ordinal) ? "Enumerate" + pascal.Substring(4) : "Enumerate" + pascal) + "Async";
                }
            }

            public string Member => ResourceMember(Resource) + "." + CsMethod;

            public string? EnumeratorMember => Pagination == null ? null : ResourceMember(Resource) + "." + EnumeratorName;
        }

        private sealed class WebhookDef
        {
            public WebhookDef(string name, string payload)
            {
                Name = name;
                Payload = payload;
            }

            public string Name { get; }

            public string Payload { get; }

            public string? Summary { get; set; }

            public string? Description { get; set; }

            /// <summary>The spec documents a required <c>X-IB-Signature</c> header (report/MO). Webhooks without one (counsel) get unsigned parsers.</summary>
            public bool Signed { get; set; }

            public string? Ack { get; set; }
        }

        private sealed class ResourceNode
        {
            public ResourceNode(string path)
            {
                Path = path;
            }

            /// <summary>Dotted path, for example <c>alimtalk.templates</c>.</summary>
            public string Path { get; }

            public string ClassName => HandWrittenResources.Contains(Path) ? Pascal(Path) + "Resource" : string.Concat(Path.Split('.').Select(Pascal)) + "Resource";

            public string PropertyName => Pascal(Path.Split('.').Last());

            public SortedDictionary<string, ResourceNode> Children { get; } = new SortedDictionary<string, ResourceNode>(StringComparer.Ordinal);

            public List<OpDef> Ops { get; } = new List<OpDef>();

            public bool HandWritten => HandWrittenResources.Contains(Path);
        }

        private sealed class ApiModel
        {
            private readonly JsonObject _parameters;
            private readonly SpecModel _models;

            public ApiModel(JsonObject spec, SpecModel models)
            {
                _models = models;
                _parameters = spec["components"]?["parameters"]?.AsObject() ?? new JsonObject();
                foreach (var path in spec["paths"]!.AsObject())
                {
                    var shared = path.Value!["parameters"] as JsonArray;
                    foreach (var entry in path.Value!.AsObject())
                    {
                        if (entry.Key == "parameters")
                        {
                            continue;
                        }

                        Ops.Add(BuildOp(path.Key, entry.Key, entry.Value!.AsObject(), shared));
                    }
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var op in Ops)
                {
                    if (!ids.Add(op.Id) || !names.Add(op.Name))
                    {
                        throw new InvalidOperationException("duplicate operation " + op.Id + " / " + op.Name);
                    }
                }

                foreach (var id in HandWritten.Keys)
                {
                    if (!ids.Contains(id))
                    {
                        throw new InvalidOperationException("hand-written operation " + id + " is not in the spec");
                    }
                }

                Root = new ResourceNode(string.Empty);
                foreach (var op in Ops)
                {
                    var node = Root;
                    var parts = op.Resource.Split('.');
                    for (var i = 0; i < parts.Length; i++)
                    {
                        var key = string.Join(".", parts.Take(i + 1));
                        if (!node.Children.TryGetValue(parts[i], out var child))
                        {
                            child = new ResourceNode(key);
                            node.Children[parts[i]] = child;
                        }

                        node = child;
                    }

                    node.Ops.Add(op);
                }

                foreach (var webhook in spec["webhooks"]?.AsObject() ?? new JsonObject())
                {
                    var post = webhook.Value!["post"]!.AsObject();
                    var name = Str(post, "x-sdk-webhook") ?? throw new InvalidOperationException("webhook " + webhook.Key + " has no x-sdk-webhook");
                    var payload = RefName(Str(post["requestBody"]?["content"]?["application/json"]?["schema"], "$ref"));
                    var def = new WebhookDef(name, payload)
                    {
                        Summary = Str(post, "summary"),
                        Description = Str(post, "description"),
                        Ack = Str(post["responses"]?["200"]?["content"]?["application/json"]?["schema"], "$ref") is string ack ? RefName(ack) : null,
                    };
                    foreach (var p in post["parameters"]?.AsArray() ?? new JsonArray())
                    {
                        var resolved = Resolve(p!.AsObject());
                        if (Str(resolved, "in") == "header" && Str(resolved, "name") == "X-IB-Signature")
                        {
                            if (!(resolved["required"] is JsonValue r && r.TryGetValue<bool>(out var req) && req))
                            {
                                throw new InvalidOperationException("webhook " + webhook.Key + ": X-IB-Signature must be required when documented (optional signatures are not supported)");
                            }

                            def.Signed = true;
                        }
                    }

                    Webhooks.Add(def);
                }
            }

            public List<OpDef> Ops { get; } = new List<OpDef>();

            public ResourceNode Root { get; }

            public List<WebhookDef> Webhooks { get; } = new List<WebhookDef>();

            public SpecModel Models => _models;

            private JsonObject Resolve(JsonObject parameter)
            {
                var reference = Str(parameter, "$ref");
                if (reference == null)
                {
                    return parameter;
                }

                if (!reference.StartsWith(ParameterPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("unsupported parameter $ref " + reference);
                }

                return _parameters[reference.Substring(ParameterPrefix.Length)]!.AsObject();
            }

            private OpDef BuildOp(string path, string method, JsonObject op, JsonArray? shared)
            {
                var id = Str(op, "operationId") ?? throw new InvalidOperationException(method + " " + path + ": no operationId");
                var op2 = new OpDef(
                    id,
                    method.ToUpperInvariant(),
                    path,
                    Str(op, "x-sdk-resource") ?? throw new InvalidOperationException(id + ": no x-sdk-resource"),
                    Str(op, "x-sdk-method") ?? throw new InvalidOperationException(id + ": no x-sdk-method"))
                {
                    Summary = Str(op, "summary"),
                    Description = Str(op, "description"),
                    Source = Str(op, "x-source"),
                };
                switch (Str(op, "x-sdk-retry"))
                {
                    case "safe":
                        op2.Safe = true;
                        break;
                    case "rate_limit_only":
                        op2.Safe = false;
                        break;
                    default:
                        throw new InvalidOperationException(id + ": x-sdk-retry must be safe or rate_limit_only");
                }

                var rate = Str(op, "x-sdk-rate");
                if (rate != null && rate != "send")
                {
                    throw new InvalidOperationException(id + ": unknown x-sdk-rate " + rate);
                }

                op2.SendBucket = rate == "send";

                var parameters = new List<JsonObject>();
                foreach (var p in (shared ?? new JsonArray()).Concat(op["parameters"]?.AsArray() ?? new JsonArray()))
                {
                    parameters.Add(Resolve(p!.AsObject()));
                }

                // path parameters in the order they appear in the path
                var pathNames = PathParam.Matches(path).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                foreach (var name in pathNames)
                {
                    var p = parameters.FirstOrDefault(x => Str(x, "in") == "path" && Str(x, "name") == name) ?? throw new InvalidOperationException(id + ": path parameter " + name + " is not declared");
                    op2.Params.Add(BuildParam(id, p));
                }

                foreach (var p in parameters.Where(x => Str(x, "in") == "header"))
                {
                    op2.Params.Add(BuildParam(id, p));
                }

                foreach (var p in parameters.Where(x => Str(x, "in") == "query"))
                {
                    op2.Params.Add(BuildParam(id, p));
                }

                if (parameters.Any(x => Str(x, "in") != "path" && Str(x, "in") != "query" && Str(x, "in") != "header"))
                {
                    throw new InvalidOperationException(id + ": unsupported parameter location");
                }

                if (op["requestBody"] is JsonObject body)
                {
                    var required = body["required"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
                    foreach (var media in body["content"]!.AsObject())
                    {
                        var schema = RefName(Str(media.Value?["schema"], "$ref"));
                        var form = media.Key.StartsWith("multipart/", StringComparison.Ordinal);
                        if (!form && media.Key != "application/json")
                        {
                            throw new InvalidOperationException(id + ": unsupported content type " + media.Key);
                        }

                        op2.Bodies.Add(new BodyDef(schema, form, required));
                    }
                }

                var ok = op["responses"]?["200"]?["content"]?["application/json"]?["schema"];
                var responseRef = Str(ok, "$ref") ?? throw new InvalidOperationException(id + ": 200 response must reference a schema");
                op2.ResponseClass = RefName(responseRef);

                var resultPath = Str(op, "x-sdk-result");
                op2.Result = Walk(op2.ResponseClass, resultPath ?? "data.data", allowMissing: resultPath == null);

                if (op["x-sdk-pagination"] is JsonObject pagination)
                {
                    op2.Pagination = BuildPagination(op2, pagination);
                }

                var options = op2.OptionalQuery.ToList();
                if (options.Count > 0)
                {
                    op2.OptionsClass = string.Concat(op2.Resource.Split('.').Select(Pascal)) + Pascal(op2.Method) + "Options";
                }

                return op2;
            }

            private static ParamDef BuildParam(string id, JsonObject p)
            {
                var name = Str(p, "name")!;
                var schema = p["schema"]?.AsObject() ?? new JsonObject();
                var def = new ParamDef(name, Str(p, "in")!)
                {
                    Required = p["required"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r,
                    Description = Str(p, "description") ?? Str(schema, "description"),
                    Unverified = Str(p, "x-unverified") ?? Str(schema, "x-unverified"),
                    Minimum = Long(schema, "minimum"),
                    Maximum = Long(schema, "maximum"),
                    MinLength = Long(schema, "minLength"),
                    MaxLength = Long(schema, "maxLength"),
                    MinItems = Long(schema, "minItems"),
                    Pattern = Str(schema, "pattern"),
                    Enum = Strings(schema, "enum"),
                    KnownValues = Strings(schema, "x-known-values") ?? Strings(schema["items"], "x-known-values"),
                    Default = Scalar(schema["default"]),
                    Format = Str(p, "x-format") ?? Str(schema, "x-format"),
                };
                if (def.Location == "path")
                {
                    def.Required = true;
                }

                var (type, _) = TypeOf(schema);
                switch (type)
                {
                    case "string":
                        if (def.Format != null && DateFormats.Contains(def.Format))
                        {
                            def.Type = "BizgoDate";
                        }
                        else if (def.Format != null && DateTimeFormats.Contains(def.Format))
                        {
                            def.Type = "BizgoDateTime";
                            def.DateTimeFormat = def.Format;
                        }

                        break;
                    case "integer":
                        def.Type = Str(schema, "format") == "int64" ? "long" : "int";
                        break;
                    case "boolean":
                        def.Type = "bool";
                        break;
                    case "array":
                        if (TypeOf(schema["items"]!.AsObject()).Type != "string")
                        {
                            throw new InvalidOperationException(id + "." + name + ": only string arrays are supported in queries");
                        }

                        def.Type = "IList<string>";
                        def.Explode = !(p["explode"] is JsonValue ev && ev.TryGetValue<bool>(out var explode) && !explode);
                        break;
                    default:
                        throw new InvalidOperationException(id + "." + name + ": unsupported parameter type " + type);
                }

                if (def.Location != "query" && def.Type != "string")
                {
                    throw new InvalidOperationException(id + "." + name + ": path/header parameters must be strings");
                }

                return def;
            }

            private PaginationDef BuildPagination(OpDef op, JsonObject p)
            {
                var style = Str(p, "style")!;
                var request = op.Params.FirstOrDefault(x => x.Location == "query" && x.ApiName == Str(p, "request") && !x.Required)
                    ?? throw new InvalidOperationException(op.Id + ": pagination request parameter must be an optional query parameter");
                var items = Walk(op.ResponseClass, Str(p, "items")!, allowMissing: false)!;
                if (!items.List)
                {
                    throw new InvalidOperationException(op.Id + ": pagination items must be a list");
                }

                var def = new PaginationDef(style, request, items);
                switch (style)
                {
                    case "cursor":
                        def.Cursor = Walk(op.ResponseClass, Str(p, "response")!, allowMissing: false);
                        break;
                    case "page":
                    case "offset":
                        var size = Str(p, "size");
                        if (size != null)
                        {
                            def.Size = op.Params.FirstOrDefault(x => x.Location == "query" && x.ApiName == size && !x.Required)
                                ?? throw new InvalidOperationException(op.Id + ": pagination size parameter must be an optional query parameter");
                        }

                        if (request.Type != "int" && request.Type != "long")
                        {
                            throw new InvalidOperationException(op.Id + ": page/offset parameter must be an integer");
                        }

                        break;
                    default:
                        throw new InvalidOperationException(op.Id + ": unknown pagination style " + style);
                }

                if (Str(p, "hasNext") is string hasNext)
                {
                    def.HasNext = Walk(op.ResponseClass, hasNext, allowMissing: false);
                }

                if (Str(p, "total") is string total)
                {
                    def.Total = Walk(op.ResponseClass, total, allowMissing: false);
                }

                if (def.Cursor != null && def.Cursor.Type.TrimEnd('?') != request.Type)
                {
                    throw new InvalidOperationException(op.Id + ": cursor type " + def.Cursor.Type + " does not match " + request.Type);
                }

                return def;
            }

            /// <summary>
            /// C# expression (from a variable named <c>response</c>) and type of the value at a dotted path (<c>data.data.items</c>,
            /// <c>data.data.list[-1].id</c>). Null when <paramref name="allowMissing"/> and a step does not exist.
            /// </summary>
            internal PathStep? Walk(string responseClass, string path, bool allowMissing)
            {
                var cls = _models.Find(responseClass);
                var expr = "response";
                var first = true;
                PathStep? step = null;
                var parts = path.Split('.');
                for (var i = 0; i < parts.Length; i++)
                {
                    var match = Regex.Match(parts[i], @"^(\w+)(\[-1\])?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (!match.Success)
                    {
                        throw new InvalidOperationException("unsupported x-sdk path " + path);
                    }

                    if (cls == null)
                    {
                        throw new InvalidOperationException(path + ": cannot step into a non-object");
                    }

                    var prop = cls.Props.FirstOrDefault(p => p.ApiName == match.Groups[1].Value);
                    if (prop == null)
                    {
                        if (allowMissing)
                        {
                            return null;
                        }

                        throw new InvalidOperationException(responseClass + ": no property at " + path);
                    }

                    expr += (first ? "." : "?.") + prop.CsName;
                    first = false;
                    var last = match.Groups[2].Success;
                    if (prop.IsList)
                    {
                        var element = prop.ElementType;
                        if (last)
                        {
                            expr = "(" + expr + ")?.LastOrDefault()";
                            cls = _models.TryFind(element);
                            step = new PathStep(expr, element, cls, false);
                        }
                        else
                        {
                            if (i != parts.Length - 1)
                            {
                                throw new InvalidOperationException(path + ": a list must be the last step (or use [-1])");
                            }

                            step = new PathStep(expr, element, _models.TryFind(element), true);
                        }
                    }
                    else
                    {
                        if (last)
                        {
                            throw new InvalidOperationException(path + ": [-1] on a non-list");
                        }

                        cls = _models.TryFind(prop.Type);
                        step = new PathStep(expr, prop.Type, cls, false);
                    }
                }

                return step;
            }
        }

        private static string ResourceMember(string resource) => string.Join(".", resource.Split('.').Select(Pascal));

        private static string Identifier(string apiName)
        {
            var name = new string(apiName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            name = char.ToLowerInvariant(name[0]) + name.Substring(1);
            return CSharpKeywords.Contains(name) ? "@" + name : name;
        }

        // ------------------------------------------------------------------ emit

        private static IEnumerable<GeneratedFile> EmitApi(ApiModel api)
        {
            yield return new GeneratedFile(OutputDirectory + "/BizgoOperation.g.cs", EmitOperations(api));
            yield return new GeneratedFile(OutputDirectory + "/BizgoClient.g.cs", EmitClient(api));
            yield return new GeneratedFile(OutputDirectory + "/Webhooks.g.cs", EmitWebhooks(api));
            foreach (var node in Flatten(api.Root))
            {
                if (node.HandWritten && node.Ops.All(o => o.IsHandWritten) && node.Children.Count == 0)
                {
                    continue;
                }

                yield return new GeneratedFile(OutputDirectory + "/Resources/" + node.ClassName + ".g.cs", EmitResource(api, node));
            }
        }

        private static IEnumerable<ResourceNode> Flatten(ResourceNode node)
        {
            foreach (var child in node.Children.Values)
            {
                yield return child;
                foreach (var grandChild in Flatten(child))
                {
                    yield return grandChild;
                }
            }
        }

        private static string EmitOperations(ApiModel api)
        {
            var w = new CodeWriter();
            w.Raw(Header);
            w.Line();
            w.Line("namespace Bizgo");
            w.Open();
            w.Line("public sealed partial class BizgoOperation");
            w.Open();
            foreach (var op in api.Ops)
            {
                w.Line("/// <summary><c>" + op.HttpMethod + " " + Xml(op.Path) + "</c> (<c>" + op.Name + "</c>).</summary>");
                w.Line("internal static readonly BizgoOperation " + op.FieldName + " = new BizgoOperation(" + string.Join(", ", new[]
                {
                    Literal(op.Id),
                    Literal(op.Name),
                    Literal(op.HttpMethod),
                    Literal(op.Path),
                    op.Safe ? "BizgoRetryPolicy.Safe" : "BizgoRetryPolicy.RateLimitOnly",
                    op.SendBucket ? "BizgoRateBucket.Send" : "BizgoRateBucket.Other",
                    op.Pagination == null ? "null" : Literal(op.Pagination.Style),
                    Literal(op.IsHandWritten ? HandWritten[op.Id].Member : op.Member),
                    op.IsHandWritten ? (HandWritten[op.Id].Enumerator is string e ? Literal(e) : "null") : (op.EnumeratorMember is string m ? Literal(m) : "null"),
                    op.IsHandWritten ? "true" : "false",
                }) + ");");
                w.Line();
            }

            w.Line("private static readonly BizgoOperation[] AllOperations =");
            w.Open();
            foreach (var op in api.Ops)
            {
                w.Line(op.FieldName + ",");
            }

            w.Dedent();
            w.Line("};");
            w.Close();
            w.Close();
            return w.ToString();
        }

        private static string EmitClient(ApiModel api)
        {
            var w = new CodeWriter();
            w.Raw(Header);
            w.Line();
            w.Line("using Bizgo.Internal;");
            w.Line();
            w.Line("namespace Bizgo");
            w.Open();
            w.Line("public sealed partial class BizgoClient");
            w.Open();
            var generated = api.Root.Children.Values.Where(n => !n.HandWritten).ToList();
            foreach (var node in generated)
            {
                w.Line("/// <summary><c>" + node.Path + ".*</c> API (" + Xml(Describe(node)) + ").</summary>");
                w.Line("public " + node.ClassName + " " + node.PropertyName + " { get; private set; } = null!;");
                w.Line();
            }

            w.Line("private void InitializeGeneratedResources(Transport transport)");
            w.Open();
            foreach (var node in generated)
            {
                w.Line(node.PropertyName + " = new " + node.ClassName + "(transport);");
            }

            w.Close();
            w.Close();
            w.Close();
            return w.ToString();
        }

        private static string Describe(ResourceNode node)
        {
            var count = Flatten(node).Sum(n => n.Ops.Count) + node.Ops.Count;
            return "operation " + count.ToString(CultureInfo.InvariantCulture) + "개";
        }

        private static string EmitResource(ApiModel api, ResourceNode node)
        {
            var w = new CodeWriter();
            w.Raw(Header);
            w.Line();
            foreach (var u in new[] { "System", "System.Collections.Generic", "System.Linq", "System.Runtime.CompilerServices", "System.Threading", "System.Threading.Tasks", "Bizgo.Internal", "Bizgo.Models" })
            {
                w.Line("using " + u + ";");
            }

            w.Line();
            w.Line("namespace Bizgo");
            w.Open();
            if (!node.HandWritten)
            {
                w.Line("/// <summary><c>client." + ResourceMember(node.Path) + "</c>: " + Xml(ResourceSummary(node)) + "</summary>");
                w.Line("/// <remarks>Generated from the spec's <c>x-sdk-resource: " + node.Path + "</c> operations. Thread-safe.</remarks>");
            }

            w.Line("public sealed partial class " + node.ClassName);
            w.Open();
            if (!node.HandWritten)
            {
                w.Line("private readonly Transport _transport;");
                w.Line();
                w.Line("internal " + node.ClassName + "(Transport transport)");
                w.Open();
                w.Line("_transport = transport;");
                foreach (var child in node.Children.Values)
                {
                    w.Line(child.PropertyName + " = new " + child.ClassName + "(transport);");
                }

                w.Close();
                w.Line();
                foreach (var child in node.Children.Values)
                {
                    w.Line("/// <summary><c>" + child.Path + ".*</c>: " + Xml(ResourceSummary(child)) + "</summary>");
                    w.Line("public " + child.ClassName + " " + child.PropertyName + " { get; }");
                    w.Line();
                }
            }
            else if (node.Children.Count > 0)
            {
                throw new InvalidOperationException("hand-written resource " + node.Path + " cannot have generated children");
            }

            foreach (var op in node.Ops.Where(o => !o.IsHandWritten))
            {
                if (node.HandWritten && HandWritten.Values.Any(h => h.Member == op.Member || h.Enumerator == op.Member))
                {
                    continue; // a hand-written method with this name wins
                }

                EmitOperation(api, w, op);
            }

            if (!node.HandWritten)
            {
                w.Line("/// <inheritdoc/>");
                w.Line("public override string ToString() => " + Literal(node.ClassName) + ";");
            }

            w.Close();

            foreach (var op in node.Ops.Where(o => !o.IsHandWritten && o.OptionsClass != null))
            {
                w.Line();
                EmitOptions(w, op);
            }

            w.Close();
            return w.ToString();
        }

        private static string ResourceSummary(ResourceNode node)
        {
            var ops = node.Ops.Select(o => o.Summary).Where(s => !string.IsNullOrEmpty(s)).Take(3).ToList();
            var text = ops.Count > 0 ? string.Join(", ", ops) : "하위 리소스";
            return node.Ops.Count > 3 ? text + " 등" : text;
        }

        private static string ParamType(ParamDef p, bool optional)
        {
            switch (p.Type)
            {
                case "IList<string>":
                    return optional ? "IList<string>?" : "IEnumerable<string>";
                case "string":
                    return optional ? "string?" : "string";
                default:
                    return optional ? p.Type + "?" : p.Type;
            }
        }

        private static string ParamNotes(ParamDef p)
        {
            var notes = new List<string>();
            if (p.Format != null)
            {
                notes.Add("형식 <c>" + Xml(p.Format) + "</c>" + (p.Type == "BizgoDate" ? "(<c>DateTime</c>/<c>DateTimeOffset</c>/<c>DateOnly</c>도 받음, 시간대가 있으면 KST 날짜)" : p.Type == "BizgoDateTime" ? "(<c>DateTime</c>/<c>DateTimeOffset</c>도 받음, KST로 변환)" : string.Empty) + ".");
            }

            if (p.Minimum != null || p.Maximum != null)
            {
                notes.Add("범위 " + (p.Minimum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + "~" + (p.Maximum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + ".");
            }

            if (p.MaxLength != null)
            {
                notes.Add("최대 " + p.MaxLength.Value.ToString(CultureInfo.InvariantCulture) + "자.");
            }

            if (p.Enum != null)
            {
                notes.Add("허용 값: " + string.Join(", ", p.Enum.Select(e => "<c>" + Xml(e) + "</c>")) + ".");
            }

            if (p.KnownValues != null)
            {
                notes.Add("알려진 값: " + string.Join(", ", p.KnownValues.Select(e => "<c>" + Xml(e) + "</c>")) + ".");
            }

            if (p.Default != null)
            {
                notes.Add("생략하면 서버 기본값 <c>" + Xml(p.Default) + "</c>.");
            }

            if (p.Unverified != null)
            {
                notes.Add("미확인: " + Inline(p.Unverified));
            }

            var text = Inline(p.Description ?? p.ApiName);
            return notes.Count == 0 ? text : text + " " + string.Join(" ", notes);
        }

        private static void EmitMethodDoc(CodeWriter w, OpDef op, BodyDef? body, string returns, bool enumerator)
        {
            w.Line("/// <summary>");
            foreach (var line in DocBlocks((enumerator ? "모든 페이지를 차례로 읽습니다: " : string.Empty) + (op.Summary ?? op.Id)))
            {
                w.Line("/// " + line);
            }

            w.Line("/// </summary>");
            w.Line("/// <remarks>");
            w.Line("/// <para><c>" + op.HttpMethod + " " + Xml(op.Path) + "</c> (operationId <c>" + op.Id + "</c>). " + (op.Safe ? "429·5xx·연결 오류는 자동 재시도합니다." : "두 번 실행되면 안 되는 요청이라 HTTP 429만 재시도합니다.") + (op.SendBucket ? " 발송 속도 제한(send 버킷)을 씁니다." : string.Empty) + "</para>");
            if (enumerator && op.Pagination != null)
            {
                var stop = op.Pagination.Style == "cursor"
                    ? "<c>hasNext</c>가 false이거나 커서가 없거나 움직이지 않으면 멈춥니다."
                    : "받은 항목이 없거나 요청한 크기보다 적거나" + (op.Pagination.HasNext != null ? " <c>hasNext</c>가 false이거나" : string.Empty) + (op.Pagination.Total != null ? " <c>total</c>에 도달하면" : " 멈추면") + " 멈춥니다.";
                w.Line("/// <para>" + op.Pagination.Style + " 방식(<c>" + op.Pagination.Request.ApiName + "</c>)으로 다음 페이지를 요청합니다. " + stop + " 옵션의 <c>" + op.Pagination.Request.PropName + "</c>은 시작 위치로 씁니다.</para>");
            }

            foreach (var block in DocBlocks(op.Description ?? string.Empty))
            {
                w.Line("/// " + (block.StartsWith("<para>", StringComparison.Ordinal) || block.StartsWith("<", StringComparison.Ordinal) ? block : "<para>" + block + "</para>"));
            }

            if (op.Source != null)
            {
                w.Line("/// <para>원문: <see href=\"" + Xml(op.Source) + "\"/></para>");
            }

            w.Line("/// </remarks>");
            foreach (var p in op.PathParams.Concat(op.HeaderParams))
            {
                w.Line("/// <param name=\"" + p.CsName.TrimStart('@') + "\">" + ParamNotes(p) + (p.Location == "path" ? " 경로에 한 세그먼트로 인코딩합니다(<c>.</c>, <c>..</c> 거부)." : " 요청 헤더로 보냅니다(한 줄 ASCII만).") + "</param>");
            }

            if (body != null)
            {
                w.Line("/// <param name=\"request\">요청 본문(<see cref=\"" + body.Schema + "\"/>). 보내기 전에 검증합니다.</param>");
            }

            foreach (var p in op.RequiredQuery)
            {
                w.Line("/// <param name=\"" + p.CsName.TrimStart('@') + "\">" + ParamNotes(p) + "</param>");
            }

            if (op.OptionsClass != null)
            {
                w.Line("/// <param name=\"options\">선택 쿼리 파라미터(<see cref=\"" + op.OptionsClass + "\"/>).</param>");
            }

            w.Line("/// <param name=\"cancellationToken\">Cancellation.</param>");
            w.Line("/// <returns>" + returns + "</returns>");
            w.Line("/// <exception cref=\"BizgoValidationException\">인자가 스펙 제약을 어겼습니다(보내기 전 검사, 메시지에 값은 없음).</exception>");
            w.Line("/// <exception cref=\"BizgoApiException\">비즈고가 요청을 거절했습니다.</exception>");
            w.Line("/// <exception cref=\"BizgoConnectionException\">네트워크 오류입니다.</exception>");
        }

        private static string Signature(OpDef op, BodyDef? body, bool enumerator)
        {
            var args = new List<string>();
            args.AddRange(op.PathParams.Select(p => "string " + p.CsName));
            if (body != null)
            {
                args.Add(body.Schema + (body.Required ? string.Empty : "?") + " request" + (body.Required ? string.Empty : " = null"));
            }

            args.AddRange(op.HeaderParams.Select(p => ParamType(p, !p.Required) + " " + p.CsName + (p.Required ? string.Empty : " = null")));
            args.AddRange(op.RequiredQuery.Select(p => ParamType(p, false) + " " + p.CsName));
            if (op.OptionsClass != null)
            {
                args.Add(op.OptionsClass + "? options = null");
            }

            args.Add((enumerator ? "[EnumeratorCancellation] " : string.Empty) + "CancellationToken cancellationToken = default");
            return string.Join(", ", args);
        }

        private static string CallArgs(OpDef op, BodyDef? body, string options)
        {
            var args = new List<string>();
            args.AddRange(op.PathParams.Select(p => p.CsName));
            if (body != null)
            {
                args.Add("request");
            }

            args.AddRange(op.HeaderParams.Select(p => p.CsName));
            args.AddRange(op.RequiredQuery.Select(p => p.CsName));
            if (op.OptionsClass != null)
            {
                args.Add(options);
            }

            args.Add("cancellationToken");
            return string.Join(", ", args);
        }

        private static (string Type, string Expr, string Doc) ResultOf(PathStep? result)
        {
            if (result == null)
            {
                return ("Task", string.Empty, "A task.");
            }

            if (result.List)
            {
                return ("Task<IReadOnlyList<" + result.Type + ">>", "Results.List(" + result.Expr + ")", "결과 목록입니다(없으면 빈 목록, null 항목 제외).");
            }

            if (result.Class != null)
            {
                return ("Task<" + result.Type + ">", "Results.Object(" + result.Expr + ")", "결과입니다(응답에 값이 없으면 빈 객체, null을 돌려주지 않음).");
            }

            switch (result.Type)
            {
                case "string":
                    return ("Task<string>", result.Expr + " ?? string.Empty", "결과 값입니다(없으면 빈 문자열).");
                case "JsonObject":
                    return ("Task<System.Text.Json.Nodes.JsonObject>", result.Expr + " ?? new System.Text.Json.Nodes.JsonObject()", "결과 JSON 객체입니다(없으면 빈 객체).");
                default:
                    return ("Task<" + result.Type + "?>", result.Expr, "결과 값입니다(응답에 값이 없으면 null).");
            }
        }

        private static void EmitOperation(ApiModel api, CodeWriter w, OpDef op)
        {
            var bodies = op.Bodies.Count == 0 ? new List<BodyDef?> { null } : op.Bodies.Cast<BodyDef?>().ToList();
            var (resultType, resultExpr, resultDoc) = ResultOf(op.Result);
            foreach (var body in bodies)
            {
                var raw = "Send" + op.FieldName + (body?.Form == true && op.Bodies.Count > 1 ? "Form" : string.Empty) + "Async";

                // public page method
                EmitMethodDoc(w, op, body, resultDoc, enumerator: false);
                w.Line("public async " + resultType + " " + op.CsMethod + "(" + Signature(op, body, false) + ")");
                w.Open();
                if (op.Result == null)
                {
                    w.Line("await " + raw + "(" + CallArgs(op, body, "options") + ").ConfigureAwait(false);");
                }
                else
                {
                    w.Line("var response = await " + raw + "(" + CallArgs(op, body, "options") + ").ConfigureAwait(false);");
                    w.Line("return " + resultExpr + ";");
                }

                w.Close();
                w.Line();

                if (op.Pagination != null && body == null)
                {
                    EmitEnumerator(w, op, raw);
                }

                EmitRaw(api, w, op, body, raw);
            }
        }

        private static void EmitEnumerator(CodeWriter w, OpDef op, string raw)
        {
            var p = op.Pagination!;
            EmitMethodDoc(w, op, null, "항목을 하나씩 돌려주는 비동기 시퀀스입니다.", enumerator: true);
            w.Line("public async IAsyncEnumerable<" + p.Items.Type + "> " + op.EnumeratorName + "(" + Signature(op, null, true) + ")");
            w.Open();
            w.Line("var page = options?.Clone() ?? new " + op.OptionsClass + "();");
            var cursor = "page." + p.Request.PropName;
            if (p.Style == "page")
            {
                w.Line(cursor + " ??= 1;");
            }
            else if (p.Style == "offset")
            {
                w.Line(cursor + " ??= 0;");
            }

            w.Line("long seen = 0;");
            w.Line("while (true)");
            w.Open();
            w.Line("var response = await " + raw + "(" + CallArgs(op, null, "page") + ").ConfigureAwait(false);");
            w.Line("var items = Results.List(" + p.Items.Expr + ");");
            w.Line("foreach (var item in items)");
            w.Open();
            w.Line("yield return item;");
            w.Close();
            w.Line();
            w.Line("seen += items.Count;");
            if (p.Style == "cursor")
            {
                var hasNext = p.HasNext != null ? "(" + p.HasNext.Expr + " ?? false)" : "items.Count > 0";
                w.Line("var next = " + p.Cursor!.Expr + ";");
                w.Line("if (!" + hasNext + " || next is null || Equals(next, " + cursor + "))");
                w.Open();
                w.Line("yield break;");
                w.Close();
                w.Line();
                w.Line(cursor + " = next;");
            }
            else
            {
                var size = p.Size != null ? "page." + p.Size.PropName : "null";
                var hasNext = p.HasNext != null ? p.HasNext.Expr : "null";
                string total;
                if (p.Total == null)
                {
                    total = "null";
                }
                else if (p.Total.Type == "string")
                {
                    total = "Paging.ParseCount(" + p.Total.Expr + ")";
                }
                else
                {
                    total = p.Total.Expr;
                }

                w.Line("if (!Paging.More(items.Count, " + size + ", " + hasNext + ", " + total + ", seen))");
                w.Open();
                w.Line("yield break;");
                w.Close();
                w.Line();
                w.Line(cursor + " += " + (p.Style == "page" ? "1" : "items.Count") + ";");
            }

            w.Close();
            w.Close();
            w.Line();
        }

        private static void EmitRaw(ApiModel api, CodeWriter w, OpDef op, BodyDef? body, string raw)
        {
            w.Line("private async Task<" + op.ResponseClass + "> " + raw + "(" + Signature(op, body, false).Replace(" = null", string.Empty).Replace(" = default", string.Empty) + ")");
            w.Open();
            if (body != null && body.Required)
            {
                w.Line("if (request is null)");
                w.Open();
                w.Line("throw new ArgumentNullException(nameof(request));");
                w.Close();
                w.Line();
            }

            w.Line("var v = new RequestValidator();");
            foreach (var p in op.PathParams.Concat(op.HeaderParams).Concat(op.RequiredQuery))
            {
                if (p.Required && !p.IsValueType)
                {
                    w.Line("v.Required(string.Empty, " + Literal(p.ApiName) + ", " + p.CsName + ");");
                }

                EmitParamChecks(w, p, p.CsName);
            }

            if (op.OptionsClass != null)
            {
                w.Line("options?.ValidateInto(v);");
            }

            if (body != null)
            {
                w.Line("request?.ValidateInto(v, string.Empty);");
            }

            w.Line("v.ThrowIfInvalid();");
            w.Line();

            // path
            var pathExpr = new List<string>();
            var last = 0;
            foreach (Match m in PathParam.Matches(op.Path))
            {
                if (m.Index > last)
                {
                    pathExpr.Add(Literal(op.Path.Substring(last, m.Index - last)));
                }

                var p = op.PathParams.First(x => x.ApiName == m.Groups[1].Value);
                pathExpr.Add("PathSegment.Escape(" + p.CsName + ", " + Literal(p.ApiName) + ")");
                last = m.Index + m.Length;
            }

            if (last < op.Path.Length)
            {
                pathExpr.Add(Literal(op.Path.Substring(last)));
            }

            w.Line("var path = " + string.Join(" + ", pathExpr) + ";");

            // query
            if (op.Params.Any(p => p.Location == "query"))
            {
                w.Line("var query = new QueryBuilder();");
                foreach (var p in op.RequiredQuery)
                {
                    w.Line(QueryAdd("query", p, p.CsName) + ";");
                }

                if (op.OptionsClass != null)
                {
                    w.Line("options?.AddTo(query);");
                }
            }

            // headers
            var headers = "null";
            if (op.HeaderParams.Any())
            {
                w.Line("var headers = new List<KeyValuePair<string, string>>();");
                foreach (var p in op.HeaderParams)
                {
                    w.Line("HeaderValue.Add(headers, " + Literal(p.ApiName) + ", " + p.CsName + ");");
                }

                headers = "headers";
            }

            // body
            var content = "null";
            if (body != null && !body.Form)
            {
                // A body with idempotencyKey and idempotencyTtl gets BizgoClient.DefaultIdempotencyTtl when only the key is set (A309 otherwise).
                var model = api.Models.Find(body.Schema);
                var key = model.Props.FirstOrDefault(p => p.ApiName == "idempotencyKey" && p.Type == "string");
                var ttl = model.Props.FirstOrDefault(p => p.ApiName == "idempotencyTtl" && p.Type == "int");
                var idempotency = key != null && ttl != null ? ", request." + key.CsName + ", request." + ttl.CsName : string.Empty;
                w.Line("var content = request is null ? null : JsonBody.Create(request, BizgoJsonContext.Default." + body.Schema + idempotency + ");");
                content = "content";
            }
            else if (body != null)
            {
                var form = api.Models.Find(body.Schema);
                w.Line("var form = new MultipartForm();");
                w.Line("if (request is not null)");
                w.Open();
                foreach (var prop in form.Props)
                {
                    var name = Literal(prop.ApiName);
                    if (prop.IsFile)
                    {
                        w.Line("await form.FileAsync(" + name + ", request." + prop.CsName + ", " + (prop.MaxBytes?.ToString(CultureInfo.InvariantCulture) ?? "FileUpload.MaxBytes") + ", cancellationToken).ConfigureAwait(false);");
                    }
                    else if (prop.IsFileList)
                    {
                        w.Line("await form.FilesAsync(" + name + ", request." + prop.CsName + ", FileUpload.MaxBytes, cancellationToken).ConfigureAwait(false);");
                    }
                    else if (prop.IsJsonPart)
                    {
                        w.Line("form.Json(" + name + ", request." + prop.CsName + ", BizgoJsonContext.Default." + prop.Type + ");");
                    }
                    else if (prop.Type == "string")
                    {
                        w.Line("form.Text(" + name + ", request." + prop.CsName + ");");
                    }
                    else if (prop.IsValueType)
                    {
                        w.Line("form.Text(" + name + ", request." + prop.CsName + ");");
                    }
                    else
                    {
                        throw new InvalidOperationException(body.Schema + "." + prop.ApiName + ": unsupported multipart field type " + prop.Type);
                    }
                }

                w.Close();
                w.Line();
                w.Line("var content = form.Build();");
                content = "content";
            }

            var cost = "1";
            if (op.SendBucket && body != null && !body.Form && api.Models.Find(body.Schema).Props.Any(p => p.ApiName == "destinations" && p.IsList))
            {
                cost = "RateCost.Destinations(request?.Destinations?.Count)";
            }

            var retry = op.Safe ? "RetryPolicy.Safe" : "RetryPolicy.RateLimitOnly";
            w.Line("return await _transport.SendAsync(BizgoOperation." + op.FieldName + ", path, " + (op.Params.Any(p => p.Location == "query") ? "query.Build()" : "null") + ", " + content + ", " + retry + ", " + cost + ", " + headers + ", BizgoJsonContext.Default." + op.ResponseClass + ", cancellationToken).ConfigureAwait(false);");
            w.Close();
            w.Line();
        }

        private static string QueryAdd(string builder, ParamDef p, string value)
        {
            switch (p.Type)
            {
                case "BizgoDate":
                    return builder + ".Add(" + Literal(p.ApiName) + ", " + value + (p.Required ? string.Empty : "?") + ".ToQuery(" + Literal(p.ApiName) + "))";
                case "BizgoDateTime":
                    return builder + ".Add(" + Literal(p.ApiName) + ", " + value + (p.Required ? string.Empty : "?") + ".Format(" + Literal(p.DateTimeFormat!) + ", " + Literal(p.ApiName) + "))";
                case "IList<string>":
                    return builder + ".Add(" + Literal(p.ApiName) + ", " + value + ", explode: " + (p.Explode ? "true" : "false") + ")";
                default:
                    return builder + ".Add(" + Literal(p.ApiName) + ", " + value + ")";
            }
        }

        private static void EmitParamChecks(CodeWriter w, ParamDef p, string value)
        {
            var name = Literal(p.ApiName);
            switch (p.Type)
            {
                case "string":
                    if (p.MinLength != null)
                    {
                        w.Line("v.MinLength(string.Empty, " + name + ", " + value + ", " + p.MinLength.Value.ToString(CultureInfo.InvariantCulture) + ");");
                    }

                    if (p.MaxLength != null)
                    {
                        w.Line("v.MaxLength(string.Empty, " + name + ", " + value + ", " + p.MaxLength.Value.ToString(CultureInfo.InvariantCulture) + ");");
                    }

                    if (p.Pattern != null)
                    {
                        w.Line("v.Pattern(string.Empty, " + name + ", " + value + ", " + Literal(p.Pattern) + ");");
                    }

                    if (p.Enum != null)
                    {
                        w.Line("v.OneOf(string.Empty, " + name + ", " + value + ", " + string.Join(", ", p.Enum.Select(Literal)) + ");");
                    }

                    break;
                case "int":
                case "long":
                    if (p.Minimum != null || p.Maximum != null)
                    {
                        w.Line("v.Range(string.Empty, " + name + ", " + value + ", " + (p.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "null") + ", " + (p.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "null") + ");");
                    }

                    break;
                case "IList<string>":
                    w.Line("v.Items(string.Empty, " + name + ", " + value + (p.Required ? "?.ToList()" : string.Empty) + ", " + (p.MinItems?.ToString(CultureInfo.InvariantCulture) ?? "null") + ", null);");
                    break;
            }
        }

        private static void EmitOptions(CodeWriter w, OpDef op)
        {
            var name = op.OptionsClass!;
            w.Line("/// <summary>Optional query parameters of <see cref=\"" + ResourceNodeClass(op) + "." + op.CsMethod + "\"/> (<c>" + op.Name + "</c>).</summary>");
            w.Line("public sealed class " + name);
            w.Open();
            var first = true;
            foreach (var p in op.OptionalQuery)
            {
                if (!first)
                {
                    w.Line();
                }

                first = false;
                w.Line("/// <summary>" + ParamNotes(p) + "</summary>");
                w.Line("public " + ParamType(p, true) + " " + p.PropName + " { get; set; }");
            }

            w.Line();
            w.Line("/// <summary>Set parameters for diagnostics; phone numbers are masked.</summary>");
            w.Line("/// <returns>A description.</returns>");
            w.Line("public override string ToString()");
            w.Open();
            w.Line("return new ModelText(" + Literal(name) + ")");
            foreach (var p in op.OptionalQuery)
            {
                var method = TextMethod(p.ApiName, p.Type);
                w.Line("    ." + method + "(" + Literal(p.ApiName) + ", " + p.PropName + ")");
            }

            w.Line("    .ToString();");
            w.Close();
            w.Line();
            w.Line("internal " + name + " Clone()");
            w.Open();
            w.Line("var copy = (" + name + ")MemberwiseClone();");
            foreach (var p in op.OptionalQuery.Where(p => p.Type == "IList<string>"))
            {
                w.Line("copy." + p.PropName + " = " + p.PropName + "?.ToList();");
            }

            w.Line("return copy;");
            w.Close();
            w.Line();
            w.Line("internal void ValidateInto(RequestValidator v)");
            w.Open();
            foreach (var p in op.OptionalQuery)
            {
                EmitParamChecks(w, p, p.PropName);
            }

            w.Close();
            w.Line();
            w.Line("internal void AddTo(QueryBuilder query)");
            w.Open();
            foreach (var p in op.OptionalQuery)
            {
                w.Line(QueryAdd("query", p, p.PropName) + ";");
            }

            w.Close();
            w.Close();
        }

        private static string ResourceNodeClass(OpDef op)
        {
            var node = new ResourceNode(op.Resource);
            return node.ClassName;
        }

        private static string EmitWebhooks(ApiModel api)
        {
            var w = new CodeWriter();
            w.Raw(Header);
            w.Line();
            w.Line("using System;");
            w.Line("using System.Collections.Generic;");
            w.Line("using Bizgo.Internal;");
            w.Line("using Bizgo.Models;");
            w.Line();
            w.Line("namespace Bizgo");
            w.Open();
            w.Line("public static partial class Webhooks");
            w.Open();
            foreach (var hook in api.Webhooks.Where(h => !HandWrittenWebhooks.Contains(h.Name)))
            {
                var method = "Parse" + Pascal(hook.Name);
                w.Line("/// <summary>Parses a <c>" + hook.Name + "</c> webhook body (" + Xml(hook.Summary ?? hook.Name) + "). " + (hook.Signed
                    ? "Does not verify the signature; use <see cref=\"WebhookReceiver\"/> for that."
                    : "상담톡 웹훅에는 서명이 없으므로 webhook secret 없이 호출합니다.") + "</summary>");
                w.Line("/// <param name=\"body\">Raw UTF-8 body.</param>");
                w.Line("/// <returns>The payload.</returns>");
                w.Line("/// <exception cref=\"BizgoWebhookVerificationException\">The body is too large, too deeply nested or not the documented JSON object.</exception>");
                w.Line("public static " + hook.Payload + " " + method + "(byte[] body) => Parse(body, BizgoJsonContext.Default." + hook.Payload + ");");
                w.Line();
                w.Line("/// <summary>Parses a <c>" + hook.Name + "</c> webhook body.</summary>");
                w.Line("/// <param name=\"body\">Body text.</param>");
                w.Line("/// <returns>The payload.</returns>");
                w.Line("public static " + hook.Payload + " " + method + "(string body) => Parse(Utf8(body), BizgoJsonContext.Default." + hook.Payload + ");");
                w.Line();
            }

            w.Close();
            w.Line();
            w.Line("public sealed partial class WebhookReceiver");
            w.Open();
            foreach (var hook in api.Webhooks.Where(h => !HandWrittenWebhooks.Contains(h.Name)))
            {
                var method = Pascal(hook.Name);
                var parse = "Webhooks.Parse" + Pascal(hook.Name);
                var policy = hook.Signed
                    ? "서명 헤더를 검증합니다."
                    : "상담톡 웹훅에는 서명이 없습니다(서명은 리포트·MO 웹훅에만 적용). 서명 헤더를 요구하거나 검사하지 않고, 있어도 무시합니다. secret 없이 쓰려면 <see cref=\"Webhooks." + "Parse" + Pascal(hook.Name) + "(byte[])\"/>를 호출하세요.";
                var ack = hook.Ack == "CounselWebhookAck" ? " 응답은 <see cref=\"Webhooks.CounselAckJson\"/>(<c>{\"code\":\"A000\",\"result\":\"Success\"}</c>)입니다." : string.Empty;
                foreach (var (headerType, bodyType, headerArg) in new[]
                {
                    ("Func<string, string?>", "byte[]", "header"),
                    ("IEnumerable<KeyValuePair<string, string>>", "byte[]", "headers"),
                    ("IEnumerable<KeyValuePair<string, string>>", "string", "headers"),
                })
                {
                    w.Line("/// <summary>" + (hook.Signed ? "Verifies, then parses" : "Parses") + " a <c>" + hook.Name + "</c> webhook (" + Xml(hook.Summary ?? hook.Name) + "). " + policy + ack + "</summary>");
                    w.Line("/// <param name=\"" + headerArg + "\">" + (headerArg == "header" ? "Header lookup by name." : "Request headers (case-insensitive names).") + "</param>");
                    w.Line("/// <param name=\"body\">" + (bodyType == "string" ? "Body text." : "Raw body.") + "</param>");
                    w.Line("/// <returns>The payload. It holds end-user identifiers and messages: do not log it.</returns>");
                    w.Line(hook.Signed
                        ? "/// <exception cref=\"BizgoWebhookVerificationException\">Verification or parsing failed; answer with HTTP 401/400.</exception>"
                        : "/// <exception cref=\"BizgoWebhookVerificationException\">The body is too large, too deeply nested or not the documented JSON object; answer with HTTP 400.</exception>");
                    w.Line("public " + hook.Payload + " " + method + "(" + headerType + " " + headerArg + ", " + bodyType + " body)");
                    w.Open();
                    if (hook.Signed)
                    {
                        w.Line("Verify(" + headerArg + ");");
                    }
                    else
                    {
                        w.Line("if (" + headerArg + " is null)");
                        w.Open();
                        w.Line("throw new ArgumentNullException(nameof(" + headerArg + "));");
                        w.Close();
                        w.Line();
                    }

                    w.Line("return " + parse + "(body);");
                    w.Close();
                    w.Line();
                }
            }

            w.Close();
            w.Close();
            return w.ToString();
        }
    }
}
