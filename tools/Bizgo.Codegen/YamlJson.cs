using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Bizgo.Codegen
{
    /// <summary>
    /// Loads YAML (the OpenAPI spec) as a <see cref="JsonNode"/> tree using the YAML 1.2 core schema:
    /// quoted scalars are strings; plain scalars become null, booleans or numbers when they look like one.
    /// </summary>
    public static class YamlJson
    {
        private static readonly Regex Integer = new Regex("^[-+]?[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex Float = new Regex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>Parses the first YAML document.</summary>
        public static JsonNode? Parse(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
            {
                return null;
            }

            return Convert(stream.Documents[0].RootNode);
        }

        private static JsonNode? Convert(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode mapping:
                    var obj = new JsonObject();
                    foreach (var entry in mapping.Children)
                    {
                        var key = ((YamlScalarNode)entry.Key).Value ?? string.Empty;
                        obj[key] = Convert(entry.Value);
                    }

                    return obj;
                case YamlSequenceNode sequence:
                    return new JsonArray(sequence.Children.Select(Convert).ToArray());
                case YamlScalarNode scalar:
                    return Scalar(scalar);
                default:
                    throw new InvalidDataException("unsupported YAML node: " + node.NodeType);
            }
        }

        private static JsonValue? Scalar(YamlScalarNode scalar)
        {
            var value = scalar.Value ?? string.Empty;
            if (scalar.Style != ScalarStyle.Plain)
            {
                return JsonValue.Create(value);
            }

            switch (value)
            {
                case "":
                case "~":
                case "null":
                case "Null":
                case "NULL":
                    return null;
                case "true":
                case "True":
                case "TRUE":
                    return JsonValue.Create(true);
                case "false":
                case "False":
                case "FALSE":
                    return JsonValue.Create(false);
            }

            if (Integer.IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            {
                return JsonValue.Create(l);
            }

            if (Float.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return JsonValue.Create(d);
            }

            return JsonValue.Create(value);
        }
    }
}
