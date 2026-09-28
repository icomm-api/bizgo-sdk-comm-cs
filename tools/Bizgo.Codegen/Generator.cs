using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bizgo.Codegen
{
    /// <summary>A generated file: path relative to the repository root, and its content (LF line endings).</summary>
    public sealed class GeneratedFile
    {
        /// <summary>Creates a generated file.</summary>
        public GeneratedFile(string path, string content)
        {
            Path = path;
            Content = content;
        }

        /// <summary>Path relative to the repository root, with forward slashes.</summary>
        public string Path { get; }

        /// <summary>File content.</summary>
        public string Content { get; }
    }

    /// <summary>
    /// Generates the C# models from <c>spec/openapi.yaml</c> and the service error-code table from
    /// <c>spec/error-codes.json</c>.
    /// <list type="bullet">
    /// <item><description>Schemas reachable from <c>SendOmniRequest</c> become <b>request</b> models: unknown JSON members are
    /// rejected and constraints (required, maxLength, x-max-bytes, pattern, enum, range, item counts) are validated.</description></item>
    /// <item><description>Every other schema becomes a lenient <b>response</b> model: unknown members are kept in
    /// <c>AdditionalProperties</c> and date-time values stay strings.</description></item>
    /// <item><description><c>*FlowItem</c> schemas collapse into one <c>MessageFlowItem</c> class with one property per channel.</description></item>
    /// <item><description><c>*FileUploadRequest</c> schemas are skipped: the SDK builds multipart bodies itself.</description></item>
    /// </list>
    /// </summary>
    public static partial class Generator
    {
        /// <summary>Directory (relative to the repository root) that holds every generated file.</summary>
        public const string OutputDirectory = "src/Bizgo.Sdk.Comm/Generated";

        private const string ModelNamespace = "Bizgo.Models";
        private const string SchemaPrefix = "#/components/schemas/";
        private const string ParameterPrefix = "#/components/parameters/";
        private const string ResponsePrefix = "#/components/responses/";

        private static readonly Regex CodeSpan = new Regex("`([^`]+)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Bold = new Regex(@"\*\*([^*]+)\*\*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Link = new Regex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>Generates every file. The result is deterministic for the same inputs.</summary>
        public static IReadOnlyList<GeneratedFile> Generate(string specYaml, string errorCodesJson)
        {
            var spec = YamlJson.Parse(specYaml)?.AsObject() ?? throw new InvalidOperationException("empty spec");
            var errorCodes = JsonNode.Parse(errorCodesJson)?.AsObject() ?? throw new InvalidOperationException("empty error codes");
            var model = new SpecModel(spec);
            var api = new ApiModel(spec, model);
            var files = new List<GeneratedFile>();
            foreach (var cls in model.Classes)
            {
                files.Add(new GeneratedFile(OutputDirectory + "/Models/" + cls.Name + ".g.cs", EmitClass(cls, model)));
            }

            files.Add(new GeneratedFile(OutputDirectory + "/BizgoJsonContext.g.cs", EmitContext(model)));
            files.Add(new GeneratedFile(OutputDirectory + "/ServiceCodes.g.cs", EmitServiceCodes(errorCodes)));
            files.AddRange(EmitApi(api));
            return files;
        }

        // ------------------------------------------------------------------ spec model

        private enum Kind
        {
            /// <summary>Used only in request bodies: unknown JSON members rejected by the serializer, validated.</summary>
            Request,

            /// <summary>Used only in responses/webhooks: lenient.</summary>
            Response,

            /// <summary>Used in both: lenient when read (unknown members kept), validated (unknown members rejected) when sent.</summary>
            Shared,

            /// <summary>multipart/form-data body: not JSON, validated, built by the multipart helper.</summary>
            Form,
        }

        private static bool Validated(Kind kind) => kind != Kind.Response;

        private static bool Lenient(Kind kind) => kind == Kind.Response || kind == Kind.Shared;

        private sealed class ClassDef
        {
            public ClassDef(string name, Kind kind)
            {
                Name = name;
                Kind = kind;
            }

            public string Name { get; }

            public Kind Kind { get; }

            public string? Description { get; set; }

            public string? Unverified { get; set; }

            public string? ChannelKey { get; set; }

            public List<PropDef> Props { get; } = new List<PropDef>();

            /// <summary>For MessageFlowItem: (channel key, message class).</summary>
            public List<KeyValuePair<string, string>> FlowMembers { get; } = new List<KeyValuePair<string, string>>();

            public bool IsFlowUnion => FlowMembers.Count > 0;

            /// <summary>The schema's <c>x-sdk-required-if</c> rules (AGENTS.md rule 11 of bizgo-api-spec), checked by <see cref="EmitRequiredIf"/>.</summary>
            public JsonArray? RequiredIf { get; set; }

            /// <summary>A request body schema: <c>ValidateInto</c> records it as the root for <c>$.</c> paths.</summary>
            public bool IsBodyRoot { get; set; }
        }

        private sealed class PropDef
        {
            public PropDef(string apiName, string csName)
            {
                ApiName = apiName;
                CsName = csName;
            }

            public string ApiName { get; }

            public string CsName { get; }

            public string Type { get; set; } = "string";

            public bool IsValueType { get; set; }

            public bool Required { get; set; }

            public string? Description { get; set; }

            public string? Unverified { get; set; }

            public long? MaxLength { get; set; }

            public long? MaxBytes { get; set; }

            public string? Charset { get; set; }

            public string? Pattern { get; set; }

            public List<string>? Enum { get; set; }

            public List<string>? KnownValues { get; set; }

            public long? Minimum { get; set; }

            public long? Maximum { get; set; }

            public long? MinItems { get; set; }

            public long? MaxItems { get; set; }

            public string? ItemClass { get; set; }

            public string? ClassRef { get; set; }

            public bool StringMap { get; set; }

            public string? Default { get; set; }

            public string? Format { get; set; }

            public bool Nullable { get; set; }

            public long? MinLength { get; set; }

            /// <summary>Fields of a referenced model that this property additionally requires (<c>allOf: [$ref, {required: [...]}]</c>).</summary>
            public List<string> ExtraRequired { get; } = new List<string>();

            /// <summary>Form only: a file part (<c>FileUpload</c>).</summary>
            public bool IsFile { get; set; }

            /// <summary>Form only: several file parts with the same name.</summary>
            public bool IsFileList { get; set; }

            /// <summary>Form only: a model sent as an <c>application/json</c> part.</summary>
            public bool IsJsonPart { get; set; }

            public bool Deprecated { get; set; }

            public bool IsList => Type.StartsWith("IList<", StringComparison.Ordinal);

            /// <summary>Element type of a list property.</summary>
            public string ElementType => Type.Substring("IList<".Length, Type.Length - "IList<".Length - 1);
        }

        private sealed class SpecModel
        {
            private readonly JsonObject _schemas;
            private readonly HashSet<string> _requestNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _responseNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _formNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _flowItems = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly HashSet<string> _bodyRoots = new HashSet<string>(StringComparer.Ordinal);

            public SpecModel(JsonObject spec)
            {
                _schemas = spec["components"]?["schemas"]?.AsObject() ?? throw new InvalidOperationException("no components.schemas");
                var responses = spec["components"]?["responses"]?.AsObject();
                ClassifySchemas(spec, responses);

                var unions = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
                foreach (var entry in _schemas)
                {
                    if (entry.Value?["oneOf"] is not JsonArray oneOf)
                    {
                        continue;
                    }

                    var flow = new ClassDef(entry.Key, Kind.Request) { Description = Str(entry.Value, "description") };
                    foreach (var member in oneOf)
                    {
                        var flowName = RefName(Str(member, "$ref"));
                        var flowSchema = _schemas[flowName]!.AsObject();
                        var props = flowSchema["properties"]!.AsObject();
                        if (props.Count != 1 || !flowName.EndsWith("FlowItem", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(entry.Key + ": only oneOf of single-key *FlowItem schemas is supported");
                        }

                        var key = props.First().Key;
                        var messageClass = RefName(Str(props.First().Value, "$ref"));
                        flow.FlowMembers.Add(new KeyValuePair<string, string>(key, messageClass));
                        _flowItems[flowName] = key;
                        ChannelKeys[messageClass] = key;
                    }

                    unions[entry.Key] = flow;
                }

                foreach (var entry in _schemas)
                {
                    var name = entry.Key;
                    if (_flowItems.ContainsKey(name))
                    {
                        continue;
                    }

                    if (unions.TryGetValue(name, out var union))
                    {
                        Classes.Add(union);
                        continue;
                    }

                    Kind kind;
                    if (_formNames.Contains(name))
                    {
                        kind = Kind.Form;
                    }
                    else if (_requestNames.Contains(name))
                    {
                        kind = _responseNames.Contains(name) ? Kind.Shared : Kind.Request;
                    }
                    else
                    {
                        kind = Kind.Response;
                    }

                    BuildClass(name, entry.Value!.AsObject(), kind);
                }

                foreach (var root in _bodyRoots)
                {
                    if (TryFind(root) is { } rootClass && Validated(rootClass.Kind))
                    {
                        rootClass.IsBodyRoot = true;
                    }
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cls in Classes)
                {
                    if (!names.Add(cls.Name))
                    {
                        throw new InvalidOperationException("duplicate class name " + cls.Name);
                    }
                }
            }

            public List<ClassDef> Classes { get; } = new List<ClassDef>();

            public Dictionary<string, string> ChannelKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            public ClassDef Find(string name) => Classes.FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException("no class " + name);

            public ClassDef? TryFind(string name) => Classes.FirstOrDefault(c => c.Name == name);

            public JsonObject Schema(string name) => _schemas[name]?.AsObject() ?? throw new InvalidOperationException("no schema " + name);

            /// <summary>
            /// Request kinds: schemas reachable from JSON request bodies (and the JSON parts of multipart bodies) are validated;
            /// those also reachable from responses or webhooks are read leniently. multipart schemas become form classes.
            /// </summary>
            private void ClassifySchemas(JsonObject spec, JsonObject? responses)
            {
                var requestRoots = new List<string>();
                var responseRoots = new List<string>();
                foreach (var path in spec["paths"]!.AsObject())
                {
                    foreach (var op in path.Value!.AsObject())
                    {
                        if (op.Key == "parameters")
                        {
                            continue;
                        }

                        if (op.Value?["requestBody"]?["content"] is JsonObject content)
                        {
                            foreach (var media in content)
                            {
                                var name = RefName(Str(media.Value?["schema"], "$ref"));
                                _bodyRoots.Add(name);
                                if (media.Key.StartsWith("multipart/", StringComparison.Ordinal))
                                {
                                    _formNames.Add(name);
                                    requestRoots.AddRange(Refs(_schemas[name]));
                                }
                                else
                                {
                                    requestRoots.Add(name);
                                }
                            }
                        }

                        foreach (var response in op.Value?["responses"]?.AsObject() ?? new JsonObject())
                        {
                            var reference = Str(response.Value, "$ref");
                            var node = reference != null && reference.StartsWith(ResponsePrefix, StringComparison.Ordinal) ? responses?[reference.Substring(ResponsePrefix.Length)] : response.Value;
                            responseRoots.AddRange(Refs(node));
                        }
                    }
                }

                foreach (var webhook in spec["webhooks"]?.AsObject() ?? new JsonObject())
                {
                    responseRoots.AddRange(Refs(webhook.Value));
                }

                _requestNames.UnionWith(Closure(requestRoots));
                _responseNames.UnionWith(Closure(responseRoots));
            }

            private HashSet<string> Closure(IEnumerable<string> roots)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<string>(roots);
                while (stack.Count > 0)
                {
                    var name = stack.Pop();
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    foreach (var reference in Refs(_schemas[name]))
                    {
                        stack.Push(reference);
                    }
                }

                return seen;
            }

            private static IEnumerable<string> Refs(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var entry in obj)
                    {
                        if (entry.Key == "$ref" && entry.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                        {
                            yield return s.Substring(SchemaPrefix.Length);
                        }
                        else
                        {
                            foreach (var r in Refs(entry.Value))
                            {
                                yield return r;
                            }
                        }
                    }
                }
                else if (node is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        foreach (var r in Refs(item))
                        {
                            yield return r;
                        }
                    }
                }
            }

            private void BuildClass(string name, JsonObject schema, Kind kind)
            {
                var cls = new ClassDef(name, kind)
                {
                    Description = Str(schema, "description"),
                    Unverified = Str(schema, "x-unverified"),
                    RequiredIf = schema["x-sdk-required-if"]?.DeepClone() as JsonArray,
                };
                if (ChannelKeys.TryGetValue(name, out var key))
                {
                    cls.ChannelKey = key;
                }

                Classes.Add(cls);

                var properties = new List<KeyValuePair<string, JsonObject>>();
                var required = new HashSet<string>(StringComparer.Ordinal);
                Collect(schema, properties, required, cls);

                foreach (var entry in properties)
                {
                    cls.Props.Add(BuildProp(cls, entry.Key, entry.Value, required.Contains(entry.Key)));
                }
            }

            private void Collect(JsonObject schema, List<KeyValuePair<string, JsonObject>> properties, HashSet<string> required, ClassDef cls)
            {
                if (schema["allOf"] is JsonArray parts)
                {
                    foreach (var part in parts)
                    {
                        var partObj = part!.AsObject();
                        var reference = Str(partObj, "$ref");
                        var resolved = reference != null ? _schemas[RefName(reference)]!.AsObject() : partObj;
                        if (cls.Description == null)
                        {
                            cls.Description = Str(resolved, "description");
                        }

                        Collect(resolved, properties, required, cls);
                    }
                }

                if (schema["required"] is JsonArray req)
                {
                    foreach (var r in req)
                    {
                        required.Add(r!.GetValue<string>());
                    }
                }

                if (schema["properties"] is JsonObject props)
                {
                    foreach (var p in props)
                    {
                        if (properties.Any(x => x.Key == p.Key))
                        {
                            properties.RemoveAll(x => x.Key == p.Key);
                        }

                        properties.Add(new KeyValuePair<string, JsonObject>(p.Key, p.Value!.AsObject()));
                    }
                }
            }

            private PropDef BuildProp(ClassDef owner, string apiName, JsonObject schema, bool required)
            {
                var csName = Pascal(apiName);
                if (csName == owner.Name || csName == "Validate" || csName == "AdditionalProperties" || csName == "Of")
                {
                    csName += "Value";
                }

                var prop = new PropDef(apiName, csName)
                {
                    Required = required,
                    Description = Str(schema, "description"),
                    Unverified = Str(schema, "x-unverified"),
                    MaxLength = Long(schema, "maxLength"),
                    MaxBytes = Long(schema, "x-max-bytes"),
                    Charset = Str(schema, "x-charset"),
                    Pattern = Str(schema, "pattern"),
                    Minimum = Long(schema, "minimum"),
                    Maximum = Long(schema, "maximum"),
                    MinItems = Long(schema, "minItems"),
                    MaxItems = Long(schema, "maxItems"),
                    Enum = Strings(schema, "enum"),
                    KnownValues = Strings(schema, "x-known-values"),
                    Default = Scalar(schema["default"]),
                    Format = Str(schema, "x-format"),
                    MinLength = Long(schema, "minLength"),
                    Deprecated = schema["deprecated"] is JsonValue dv && dv.TryGetValue<bool>(out var deprecated) && deprecated,
                };

                var reference = Str(schema, "$ref");
                if (reference == null && schema["allOf"] is JsonArray allOf)
                {
                    // allOf: [$ref, {required: [...]}] -> the referenced model, plus required fields checked by the owner
                    foreach (var part in allOf)
                    {
                        var partRef = Str(part, "$ref");
                        if (partRef != null)
                        {
                            reference = reference == null ? partRef : throw new InvalidOperationException(owner.Name + "." + apiName + ": allOf with several $ref");
                        }
                        else if (part?["required"] is JsonArray extra)
                        {
                            prop.ExtraRequired.AddRange(extra.Select(x => x!.GetValue<string>()));
                        }
                        else if (part is JsonObject other && other.Any(e => e.Key != "type"))
                        {
                            throw new InvalidOperationException(owner.Name + "." + apiName + ": unsupported allOf part");
                        }
                    }
                }

                if (reference != null)
                {
                    var target = RefName(reference);
                    var targetSchema = _schemas[target]!.AsObject();
                    if (prop.Description == null)
                    {
                        prop.Description = Str(targetSchema, "description");
                    }

                    prop.Type = target;
                    if (Validated(owner.Kind))
                    {
                        prop.ClassRef = target;
                    }

                    prop.IsJsonPart = owner.Kind == Kind.Form;
                    return prop;
                }

                var (type, nullable) = TypeOf(schema);
                prop.Nullable = nullable;
                switch (type)
                {
                    case "string":
                        if (Str(schema, "format") == "binary")
                        {
                            prop.Type = owner.Kind == Kind.Form ? "FileUpload" : throw new InvalidOperationException(owner.Name + "." + apiName + ": binary outside multipart");
                            prop.IsFile = true;
                            break;
                        }

                        prop.Type = "string";
                        break;
                    case "integer":
                        prop.Type = owner.Kind != Kind.Response && Str(schema, "format") != "int64" ? "int" : "long";
                        prop.IsValueType = true;
                        break;
                    case "number":
                        prop.Type = "double";
                        prop.IsValueType = true;
                        break;
                    case "boolean":
                        prop.Type = "bool";
                        prop.IsValueType = true;
                        break;
                    case "array":
                        var items = schema["items"]?.AsObject() ?? throw new InvalidOperationException(owner.Name + "." + apiName + ": array without items");
                        var itemRef = Str(items, "$ref");
                        if (itemRef != null)
                        {
                            var itemName = RefName(itemRef);
                            prop.Type = "IList<" + itemName + ">";
                            if (Validated(owner.Kind))
                            {
                                prop.ItemClass = itemName;
                            }
                        }
                        else if (TypeOf(items).Type == "string" && Str(items, "format") == "binary")
                        {
                            prop.Type = owner.Kind == Kind.Form ? "IList<FileUpload>" : throw new InvalidOperationException(owner.Name + "." + apiName + ": binary outside multipart");
                            prop.IsFileList = true;
                        }
                        else if (TypeOf(items).Type == "string")
                        {
                            prop.Type = "IList<string>";
                        }
                        else if (TypeOf(items).Type == "object" && items["properties"] is null)
                        {
                            prop.Type = "IList<JsonObject>";
                        }
                        else
                        {
                            throw new InvalidOperationException(owner.Name + "." + apiName + ": unsupported array item type");
                        }

                        break;
                    case "object":
                        if (schema["properties"] is JsonObject)
                        {
                            var nestedName = owner.Name + Pascal(apiName);
                            BuildClass(nestedName, schema, owner.Kind);
                            prop.Type = nestedName;
                            if (Validated(owner.Kind))
                            {
                                prop.ClassRef = nestedName;
                            }
                        }
                        else if (schema["additionalProperties"] is JsonObject additional && Str(additional, "type") == "string")
                        {
                            prop.Type = "IDictionary<string, string>";
                            prop.StringMap = true;
                        }
                        else
                        {
                            prop.Type = "JsonObject";
                        }

                        break;
                    default:
                        throw new InvalidOperationException(owner.Name + "." + apiName + ": unsupported type " + type);
                }

                return prop;
            }
        }

        private static (string Type, bool Nullable) TypeOf(JsonObject schema)
        {
            var type = schema["type"];
            if (type is JsonArray array)
            {
                var names = array.Select(t => t!.GetValue<string>()).ToList();
                var nonNull = names.Where(n => n != "null").ToList();
                if (nonNull.Count != 1)
                {
                    throw new InvalidOperationException("unsupported type union");
                }

                return (nonNull[0], names.Contains("null"));
            }

            return (type?.GetValue<string>() ?? "object", false);
        }

        private static string RefName(string? reference)
        {
            if (reference == null || !reference.StartsWith(SchemaPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unsupported $ref " + reference);
            }

            return reference.Substring(SchemaPrefix.Length);
        }

        private static string? Str(JsonNode? node, string key)
        {
            var value = node?[key];
            return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }

        private static long? Long(JsonNode? node, string key)
        {
            var value = node?[key];
            if (value is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l))
                {
                    return l;
                }

                if (v.TryGetValue<int>(out var i))
                {
                    return i;
                }
            }

            return null;
        }

        private static List<string>? Strings(JsonNode? node, string key)
        {
            return node?[key] is JsonArray array ? array.Select(x => Scalar(x) ?? "null").ToList() : null;
        }

        private static string? Scalar(JsonNode? node)
        {
            if (node is not JsonValue v)
            {
                return null;
            }

            if (v.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (v.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }

            if (v.TryGetValue<long>(out var l))
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }

            if (v.TryGetValue<double>(out var d))
            {
                return d.ToString("R", CultureInfo.InvariantCulture);
            }

            return v.ToJsonString();
        }

        /// <summary>C# property name of a messageFlow channel key.</summary>
        private static string FlowProperty(string key)
        {
            switch (key)
            {
                case "brandmessage":
                    return "BrandMessage";
                case "navertalk":
                    return "NaverTalk";
                default:
                    return Pascal(key);
            }
        }

        /// <summary><c>templateCode</c> → <c>TemplateCode</c>; <c>phone_number</c> → <c>PhoneNumber</c> (the JSON name is kept by <c>[JsonPropertyName]</c>).</summary>
        private static string Pascal(string name)
        {
            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
        }

        // ------------------------------------------------------------------ emitters

        private const string Header =
            "// <auto-generated>\n" +
            "// Generated from spec/openapi.yaml by tools/Bizgo.Codegen. Do not edit.\n" +
            "// Regenerate with: dotnet run --project tools/Bizgo.Codegen\n" +
            "// </auto-generated>\n" +
            "#nullable enable\n";

        /// <summary>API names of phone number fields: masked in <c>ToString()</c> (SDK-DESIGN §12.11).</summary>
        internal static readonly HashSet<string> PhoneFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "to", "from", "phoneNumber", "phoneNumbers", "phone_number", "originator", "callback", "dialPhoneNumber", "telNumber", "tel", "unsubscribePhoneNumber",
        };

        /// <summary>API names of free-text fields: only their length is shown in <c>ToString()</c> (also any string field ending in Message/Content/Text/Comment).</summary>
        internal static readonly HashSet<string> ContentFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "content", "contents", "text", "comment", "comments", "message", "extra", "chatExtra",
        };

        /// <summary>API names of fields about a person (end-user name, nickname, e-mail, address): masked in <c>ToString()</c>.</summary>
        internal static readonly HashSet<string> PersonFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "userName", "nickname", "nickName", "email", "roadAddress", "detailAddress",
        };

        private static readonly string[] ContentSuffixes = { "Message", "Content", "Text", "Comment" };

        /// <summary>The <c>ModelText</c> method that shows a property safely.</summary>
        internal static string TextMethod(string apiName, string type)
        {
            var isString = type == "string";
            var isList = type.StartsWith("IList<", StringComparison.Ordinal) || type.StartsWith("IDictionary<", StringComparison.Ordinal);
            if (PhoneFields.Contains(apiName))
            {
                return isString ? "Phone" : type == "IList<string>" ? "Phones" : isList ? "Count" : "Add";
            }

            if (PersonFields.Contains(apiName) && isString)
            {
                return "Masked";
            }

            var contentLike = ContentFields.Contains(apiName)
                || ContentSuffixes.Any(suffix => apiName.EndsWith(suffix, StringComparison.Ordinal));
            if (contentLike && isString)
            {
                return "Length";
            }

            return isList ? "Count" : "Add";
        }

        private static string EmitClass(ClassDef cls, SpecModel model)
        {
            var usings = new SortedSet<string>(StringComparer.Ordinal) { "Bizgo.Internal" };
            if (cls.Kind != Kind.Form)
            {
                usings.Add("System.Text.Json.Serialization");
            }

            if (cls.Props.Any(p => p.Type.StartsWith("IList<", StringComparison.Ordinal) || p.Type.StartsWith("IDictionary<", StringComparison.Ordinal)) || Lenient(cls.Kind))
            {
                usings.Add("System.Collections.Generic");
            }

            if (cls.Props.Any(p => p.Type.Contains("JsonObject", StringComparison.Ordinal)))
            {
                usings.Add("System.Text.Json.Nodes");
            }

            if (Lenient(cls.Kind))
            {
                usings.Add("System.Text.Json");
            }

            if (cls.IsFlowUnion)
            {
                usings.Add("System");
            }

            var w = new CodeWriter();
            w.Raw(Header);
            w.Line();
            foreach (var u in usings.OrderBy(u => u.StartsWith("System", StringComparison.Ordinal) ? 0 : 1).ThenBy(u => u, StringComparer.Ordinal))
            {
                w.Line("using " + u + ";");
            }

            w.Line();
            w.Line("namespace " + ModelNamespace);
            w.Open();

            var summary = DocBlocks(cls.Description ?? cls.Name);
            var remarks = new List<string>();
            switch (cls.Kind)
            {
                case Kind.Request:
                    remarks.Add(cls.IsFlowUnion
                        ? "요청 모델입니다. 보통 직접 만들지 않고 SDK 메서드가 채널 메시지를 <see cref=\"Of(IChannelMessage)\"/>로 감쌉니다."
                        : "요청 모델입니다. 설정한 속성만 직렬화되고(스펙 기본값을 임의로 보내지 않음), 보내기 전에 <see cref=\"Validate\"/>로 검증됩니다. JSON으로 읽을 때 모델에 없는 필드는 거부합니다.");
                    break;
                case Kind.Shared:
                    remarks.Add("요청과 응답에 함께 쓰는 모델입니다. 응답으로 읽을 때 스펙에 없는 필드는 <see cref=\"AdditionalProperties\"/>에 보관되고, 요청으로 보낼 때는 <see cref=\"Validate\"/>가 그런 필드를 거부합니다. 설정한 속성만 직렬화됩니다.");
                    break;
                case Kind.Form:
                    remarks.Add("multipart/form-data 요청입니다. 파일은 한 번만 읽어 재시도에 재사용하고, 모델 속성은 JSON 파트로 보냅니다. 보내기 전에 <see cref=\"Validate\"/>로 검증됩니다.");
                    break;
                default:
                    remarks.Add("응답 모델입니다. 스펙에 없는 필드는 <see cref=\"AdditionalProperties\"/>에 보관되고, 시각 값은 서버가 보낸 문자열 그대로 둡니다.");
                    break;
            }

            if (cls.ChannelKey != null)
            {
                remarks.Add("<c>messageFlow[]." + cls.ChannelKey + "</c>로 보냅니다.");
            }

            if (cls.Unverified != null)
            {
                remarks.Add("미확인: " + Inline(cls.Unverified));
            }

            WriteDoc(w, summary, remarks);
            if (cls.Kind == Kind.Request)
            {
                w.Line("[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]");
            }
            else if (Lenient(cls.Kind))
            {
                w.Line("[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]");
            }

            w.Line("public sealed partial class " + cls.Name + (cls.ChannelKey != null ? " : IChannelMessage" : string.Empty));
            w.Open();

            if (cls.IsFlowUnion)
            {
                EmitFlowUnion(w, cls, model);
            }
            else
            {
                var first = true;
                foreach (var prop in cls.Props)
                {
                    if (!first)
                    {
                        w.Line();
                    }

                    first = false;
                    EmitProperty(w, cls, prop);
                }

                if (Lenient(cls.Kind))
                {
                    if (!first)
                    {
                        w.Line();
                    }

                    w.Line("/// <summary>스펙에 없는(서버에 새로 추가된) 필드입니다. 응답 원문에 개인정보가 있을 수 있으니 그대로 로그에 남기지 마세요.</summary>");
                    w.Line("[JsonExtensionData]");
                    w.Line("public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }");
                }

                if (cls.ChannelKey != null)
                {
                    w.Line();
                    w.Line("/// <inheritdoc/>");
                    w.Line("string IChannelMessage.ChannelKey => " + Literal(cls.ChannelKey) + ";");
                }

                if (Validated(cls.Kind))
                {
                    w.Line();
                    EmitValidate(w, cls, model);
                }
            }

            w.Line();
            EmitToString(w, cls);
            w.Close();
            w.Close();
            return w.ToString();
        }

        private static void EmitToString(CodeWriter w, ClassDef cls)
        {
            w.Line("/// <summary>Set properties for diagnostics. Phone numbers are masked (<c>010****0000</c>), free text shows only its length, <c>AdditionalProperties</c> is left out.</summary>");
            w.Line("/// <returns>For example <c>" + Xml(cls.Name) + "(...)</c>.</returns>");
            w.Line("public override string ToString()");
            w.Open();
            w.Line("return new ModelText(" + Literal(cls.Name) + ")");
            if (cls.IsFlowUnion)
            {
                foreach (var member in cls.FlowMembers)
                {
                    w.Line("    .Add(" + Literal(member.Key) + ", " + FlowProperty(member.Key) + ")");
                }
            }
            else
            {
                foreach (var p in cls.Props)
                {
                    var method = TextMethod(p.ApiName, p.Type);
                    w.Line("    ." + method + "(" + Literal(p.ApiName) + ", " + p.CsName + ")");
                }
            }

            w.Line("    .ToString();");
            w.Close();
        }

        private static void EmitProperty(CodeWriter w, ClassDef cls, PropDef prop)
        {
            var notes = new List<string>();
            if (Validated(cls.Kind) && prop.Required)
            {
                notes.Add("필수입니다.");
            }

            if (prop.ExtraRequired.Count > 0 && Validated(cls.Kind))
            {
                notes.Add("이 속성에서는 " + string.Join(", ", prop.ExtraRequired.Select(r => "<c>" + Xml(r) + "</c>")) + "도 필수입니다.");
            }

            if (prop.IsJsonPart)
            {
                notes.Add("JSON 문자열 파트(<c>application/json</c>)로 보냅니다.");
            }

            if (prop.Deprecated)
            {
                notes.Add("스펙에서 더 이상 쓰지 않는(deprecated) 필드입니다.");
            }

            if (prop.MinLength != null)
            {
                notes.Add("최소 " + prop.MinLength.Value.ToString(CultureInfo.InvariantCulture) + "자입니다.");
            }

            if (prop.MaxLength != null)
            {
                notes.Add("최대 " + prop.MaxLength.Value.ToString(CultureInfo.InvariantCulture) + "자입니다.");
            }

            if (prop.MaxBytes != null)
            {
                var charset = prop.Charset ?? "UTF-8";
                notes.Add("최대 " + prop.MaxBytes.Value.ToString(CultureInfo.InvariantCulture) + "byte입니다(" + charset + (charset.Equals("EUC-KR", StringComparison.OrdinalIgnoreCase) ? ", 코드 페이지 949로 셈. 표현할 수 없는 문자(이모지 등)는 거부" : string.Empty) + ").");
            }

            if (prop.Pattern != null)
            {
                notes.Add("형식: <c>" + Xml(prop.Pattern) + "</c>");
            }

            if (prop.Minimum != null || prop.Maximum != null)
            {
                notes.Add("범위: " + (prop.Minimum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + "~" + (prop.Maximum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
            }

            if (prop.MinItems != null || prop.MaxItems != null)
            {
                notes.Add("항목 수: " + (prop.MinItems?.ToString(CultureInfo.InvariantCulture) ?? "0") + "~" + (prop.MaxItems?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
            }

            if (prop.Enum != null)
            {
                notes.Add("허용 값: " + string.Join(", ", prop.Enum.Select(e => "<c>" + Xml(e) + "</c>")));
            }

            if (prop.KnownValues != null)
            {
                notes.Add("알려진 값: " + string.Join(", ", prop.KnownValues.Select(e => "<c>" + Xml(e) + "</c>")));
            }

            if (prop.Format != null)
            {
                notes.Add("형식: <c>" + Xml(prop.Format) + "</c>");
            }

            if (prop.Default != null && Validated(cls.Kind))
            {
                notes.Add("생략하면 서버 기본값 <c>" + Xml(prop.Default) + "</c>이 적용됩니다(SDK는 보내지 않음).");
            }

            if (prop.Unverified != null)
            {
                notes.Add("미확인: " + Inline(prop.Unverified));
            }

            WriteDoc(w, DocBlocks(prop.Description ?? prop.ApiName), notes.Count == 0 ? new List<string>() : new List<string> { string.Join(" ", notes) });
            if (cls.Kind != Kind.Form)
            {
                w.Line("[JsonPropertyName(" + Literal(prop.ApiName) + ")]");
            }

            w.Line("public " + prop.Type + "? " + prop.CsName + " { get; set; }");
        }

        private static void EmitValidate(CodeWriter w, ClassDef cls, SpecModel model)
        {
            w.Line("/// <summary>보내기 전 검증을 실행합니다. 발송 메서드는 이 검증을 자동으로 실행합니다.</summary>");
            w.Line("/// <exception cref=\"BizgoValidationException\">필드가 없거나 제약을 어겼습니다. 메시지에는 필드 경로와 이유만 있고 입력값은 없습니다.</exception>");
            w.Line("public void Validate()");
            w.Open();
            w.Line("var validator = new RequestValidator();");
            w.Line("ValidateInto(validator, string.Empty);");
            w.Line("validator.ThrowIfInvalid();");
            w.Close();
            w.Line();
            w.Line("internal void ValidateInto(RequestValidator v, string path)");
            w.Open();
            if (cls.IsBodyRoot)
            {
                // the first model validated with this validator is the request body: x-sdk-required-if "$." paths start here
                w.Line("v.Root ??= this;");
            }

            if (cls.IsFlowUnion)
            {
                var keys = string.Join(", ", cls.FlowMembers.Select(m => m.Key));
                var count = string.Join(" + ", cls.FlowMembers.Select(m => "(" + FlowProperty(m.Key) + " is null ? 0 : 1)"));
                w.Line("if (" + count + " != 1)");
                w.Open();
                w.Line("v.Add(path, " + Literal("messageFlow 항목에는 채널 키(" + keys + ") 중 정확히 하나가 있어야 합니다") + ");");
                w.Close();
                w.Line();
                foreach (var member in cls.FlowMembers)
                {
                    w.Line(FlowProperty(member.Key) + "?.ValidateInto(v, RequestValidator.Child(path, " + Literal(member.Key) + "));");
                }
            }
            else
            {
                if (cls.Kind == Kind.Shared)
                {
                    w.Line("v.Unknown(path, AdditionalProperties);");
                }

                foreach (var p in cls.Props)
                {
                    var name = Literal(p.ApiName);
                    if (p.Required)
                    {
                        w.Line("v.Required(path, " + name + ", " + p.CsName + ");");
                    }

                    if (p.Type == "string")
                    {
                        if (p.MinLength != null)
                        {
                            w.Line("v.MinLength(path, " + name + ", " + p.CsName + ", " + p.MinLength.Value.ToString(CultureInfo.InvariantCulture) + ");");
                        }

                        if (p.MaxLength != null)
                        {
                            w.Line("v.MaxLength(path, " + name + ", " + p.CsName + ", " + p.MaxLength.Value.ToString(CultureInfo.InvariantCulture) + ");");
                        }

                        if (p.MaxBytes != null)
                        {
                            var method = string.Equals(p.Charset, "EUC-KR", StringComparison.OrdinalIgnoreCase) ? "MaxBytesEucKr" : "MaxBytesUtf8";
                            w.Line("v." + method + "(path, " + name + ", " + p.CsName + ", " + p.MaxBytes.Value.ToString(CultureInfo.InvariantCulture) + ");");
                        }

                        if (p.Pattern != null)
                        {
                            w.Line("v.Pattern(path, " + name + ", " + p.CsName + ", " + Literal(p.Pattern) + ");");
                        }

                        if (p.Enum != null)
                        {
                            w.Line("v.OneOf(path, " + name + ", " + p.CsName + ", " + string.Join(", ", p.Enum.Select(Literal)) + ");");
                        }
                    }
                    else if (p.IsValueType && (p.Minimum != null || p.Maximum != null))
                    {
                        w.Line("v.Range(path, " + name + ", " + p.CsName + ", " + (p.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "null") + ", " + (p.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "null") + ");");
                    }
                    else if (p.Type.StartsWith("IList<", StringComparison.Ordinal))
                    {
                        w.Line("v.Items(path, " + name + ", " + p.CsName + ", " + (p.MinItems?.ToString(CultureInfo.InvariantCulture) ?? "null") + ", " + (p.MaxItems?.ToString(CultureInfo.InvariantCulture) ?? "null") + ");");
                        if (p.ItemClass != null)
                        {
                            w.Line("if (" + p.CsName + " is not null)");
                            w.Open();
                            w.Line("for (var i = 0; i < " + p.CsName + ".Count; i++)");
                            w.Open();
                            w.Line(p.CsName + "[i]?.ValidateInto(v, RequestValidator.Index(path, " + name + ", i));");
                            w.Close();
                            w.Close();
                            w.Line();
                        }
                    }
                    else if (p.StringMap)
                    {
                        w.Line("v.StringMap(path, " + name + ", " + p.CsName + ");");
                    }
                    else if (p.ClassRef != null)
                    {
                        w.Line(p.CsName + "?.ValidateInto(v, RequestValidator.Child(path, " + name + "));");
                        foreach (var extra in p.ExtraRequired)
                        {
                            var target = Pascal(extra);
                            w.Line("if (" + p.CsName + " is not null)");
                            w.Open();
                            w.Line("v.Required(RequestValidator.Child(path, " + name + "), " + Literal(extra) + ", " + p.CsName + "." + target + ");");
                            w.Close();
                            w.Line();
                        }
                    }
                }
            }

            EmitRequiredIf(w, cls, model);
            w.Close();
        }

        private static void EmitFlowUnion(CodeWriter w, ClassDef cls, SpecModel model)
        {
            foreach (var member in cls.FlowMembers)
            {
                w.Line("/// <summary><c>" + member.Key + "</c> 채널 메시지(<see cref=\"" + member.Value + "\"/>)입니다.</summary>");
                w.Line("[JsonPropertyName(" + Literal(member.Key) + ")]");
                w.Line("public " + member.Value + "? " + FlowProperty(member.Key) + " { get; set; }");
                w.Line();
            }

            w.Line("/// <summary>채널 메시지를 <c>messageFlow</c> 항목(<c>{\"sms\": {...}}</c> 등)으로 감쌉니다.</summary>");
            w.Line("/// <param name=\"message\">채널 메시지입니다.</param>");
            w.Line("/// <returns>채널 키가 하나인 항목입니다.</returns>");
            w.Line("/// <exception cref=\"ArgumentNullException\"><paramref name=\"message\"/>가 null입니다.</exception>");
            w.Line("/// <exception cref=\"ArgumentException\">SDK가 모르는 <see cref=\"IChannelMessage\"/> 구현입니다.</exception>");
            w.Line("public static " + cls.Name + " Of(IChannelMessage message)");
            w.Open();
            w.Line("switch (message)");
            w.Open();
            foreach (var member in cls.FlowMembers)
            {
                w.Line("case " + member.Value + " m:");
                w.Line("    return new " + cls.Name + " { " + FlowProperty(member.Key) + " = m };");
            }

            w.Line("case null:");
            w.Line("    throw new ArgumentNullException(nameof(message));");
            w.Line("default:");
            w.Line("    throw new ArgumentException(\"지원하지 않는 채널 메시지 형식입니다: \" + message.GetType().Name, nameof(message));");
            w.Close();
            w.Close();
            w.Line();
            EmitValidate(w, cls, model);
        }

        private static string EmitContext(SpecModel model)
        {
            var w = new CodeWriter();
            w.Raw(Header);
            w.Line();
            w.Line("using System.Text.Json.Serialization;");
            w.Line("using Bizgo.Models;");
            w.Line();
            w.Line("namespace Bizgo.Internal");
            w.Open();
            w.Line("/// <summary>System.Text.Json source-generated metadata for every model (no reflection; trimming/AOT safe).</summary>");
            w.Line("[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 64)]");
            foreach (var cls in model.Classes.Where(c => c.Kind != Kind.Form))
            {
                w.Line("[JsonSerializable(typeof(" + cls.Name + "))]");
            }

            w.Line("internal sealed partial class BizgoJsonContext : JsonSerializerContext");
            w.Open();
            w.Close();
            w.Close();
            return w.ToString();
        }

        private static string EmitServiceCodes(JsonObject errorCodes)
        {
            var w = new CodeWriter();
            w.Raw(
                "// <auto-generated>\n" +
                "// Generated from spec/error-codes.json by tools/Bizgo.Codegen. Do not edit.\n" +
                "// Regenerate with: dotnet run --project tools/Bizgo.Codegen\n" +
                "// </auto-generated>\n" +
                "#nullable enable\n");
            w.Line();
            w.Line("using System;");
            w.Line("using System.Collections.Generic;");
            w.Line();
            w.Line("namespace Bizgo.Internal");
            w.Open();
            w.Line("/// <summary>Service-layer (<c>data.code</c>) codes: documented HTTP status and Korean description.</summary>");
            w.Line("internal static partial class ServiceCodes");
            w.Open();
            w.Line("/// <summary>Source of the table.</summary>");
            w.Line("internal const string Source = " + Literal(Str(errorCodes, "source") ?? string.Empty) + ";");
            w.Line();
            w.Line("private static readonly Dictionary<string, ServiceCode> Table = new Dictionary<string, ServiceCode>(StringComparer.Ordinal)");
            w.Open();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var entry in errorCodes["service"]!.AsArray())
            {
                var code = Str(entry, "code")!;
                var status = Long(entry, "httpStatus") ?? 0;
                var text = Str(entry, "descriptionKo") ?? Str(entry, "result") ?? string.Empty;
                if (!seen.ContainsKey(code))
                {
                    order.Add(code);
                }

                // Later rows win, like a dict literal in the Python reference.
                seen[code] = "[" + Literal(code) + "] = new ServiceCode(" + status.ToString(CultureInfo.InvariantCulture) + ", " + Literal(text) + "),";
            }

            foreach (var code in order)
            {
                w.Line(seen[code]);
            }

            w.Dedent();
            w.Line("};");
            w.Close();
            w.Close();
            return w.ToString();
        }

        // ------------------------------------------------------------------ docs

        private static void WriteDoc(CodeWriter w, List<string> summary, List<string> remarks)
        {
            w.Line("/// <summary>");
            foreach (var line in summary)
            {
                w.Line("/// " + line);
            }

            w.Line("/// </summary>");
            if (remarks.Count > 0)
            {
                w.Line("/// <remarks>");
                for (var i = 0; i < remarks.Count; i++)
                {
                    w.Line("/// " + (remarks.Count > 1 ? "<para>" + remarks[i] + "</para>" : remarks[i]));
                }

                w.Line("/// </remarks>");
            }
        }

        /// <summary>Converts a Markdown-ish description into XML doc lines.</summary>
        private static List<string> DocBlocks(string text)
        {
            var lines = text.Replace("\r", string.Empty).Split('\n').Select(l => l.TrimEnd()).ToList();
            var blocks = new List<List<string>>();
            var current = new List<string>();
            foreach (var line in lines)
            {
                if (line.Trim().Length == 0)
                {
                    if (current.Count > 0)
                    {
                        blocks.Add(current);
                        current = new List<string>();
                    }
                }
                else
                {
                    current.Add(line);
                }
            }

            if (current.Count > 0)
            {
                blocks.Add(current);
            }

            var output = new List<string>();
            var single = blocks.Count == 1 && !blocks[0].Any(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal) || l.TrimStart().StartsWith('|'));
            foreach (var block in blocks)
            {
                var text2 = new List<string>();
                var i = 0;
                while (i < block.Count)
                {
                    var line = block[i].Trim();
                    if (line.StartsWith("- ", StringComparison.Ordinal))
                    {
                        Flush(output, text2, single);
                        output.Add("<list type=\"bullet\">");
                        while (i < block.Count && block[i].Trim().StartsWith("- ", StringComparison.Ordinal))
                        {
                            var item = block[i].Trim().Substring(2);
                            i++;
                            while (i < block.Count && block[i].StartsWith("  ", StringComparison.Ordinal) && !block[i].Trim().StartsWith("- ", StringComparison.Ordinal))
                            {
                                item += " " + block[i].Trim();
                                i++;
                            }

                            output.Add("<item><description>" + Inline(item) + "</description></item>");
                        }

                        output.Add("</list>");
                        continue;
                    }

                    if (line.StartsWith('|'))
                    {
                        Flush(output, text2, single);
                        output.Add("<code>");
                        while (i < block.Count && block[i].Trim().StartsWith('|'))
                        {
                            output.Add(Xml(block[i].Trim()));
                            i++;
                        }

                        output.Add("</code>");
                        continue;
                    }

                    text2.Add(line);
                    i++;
                }

                Flush(output, text2, single);
            }

            return output;
        }

        private static void Flush(List<string> output, List<string> text, bool single)
        {
            if (text.Count == 0)
            {
                return;
            }

            var joined = Inline(string.Join(" ", text));
            output.Add(single ? joined : "<para>" + joined + "</para>");
            text.Clear();
        }

        private static string Inline(string text)
        {
            var escaped = Xml(text.Replace("\r", string.Empty).Replace("\n", " ").Trim());
            escaped = Link.Replace(escaped, "<see href=\"$2\">$1</see>");
            escaped = CodeSpan.Replace(escaped, "<c>$1</c>");
            escaped = Bold.Replace(escaped, "<b>$1</b>");
            return escaped;
        }

        private static string Xml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string Literal(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        private sealed class CodeWriter
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private int _indent;

            public void Raw(string text) => _sb.Append(text);

            public void Line(string text = "")
            {
                if (text.Length > 0)
                {
                    _sb.Append(' ', _indent * 4);
                }

                _sb.Append(text).Append('\n');
            }

            public void Open()
            {
                Line("{");
                _indent++;
            }

            public void Close()
            {
                if (_sb.Length >= 2 && _sb[_sb.Length - 1] == '\n' && _sb[_sb.Length - 2] == '\n')
                {
                    _sb.Length--;
                }

                _indent--;
                Line("}");
            }

            public void Dedent() => _indent--;

            public override string ToString() => _sb.ToString();
        }
    }
}
