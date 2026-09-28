using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bizgo.Internal;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>Consistency with the vendored OpenAPI spec.</summary>
    public class SpecTests
    {
        public static IEnumerable<object[]> RequestExamples()
        {
            var examples = Repo.Spec["paths"]!["/api/comm/v1/send/omni"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!.AsObject();
            return examples.Select(e => new object[] { e.Key });
        }

        [Theory]
        [MemberData(nameof(RequestExamples))]
        public void Spec_request_examples_validate_and_round_trip(string name)
        {
            var value = Repo.Spec["paths"]!["/api/comm/v1/send/omni"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]![name]!["value"]!;

            var request = SendOmniRequest.FromJson(value.ToJsonString());

            JsonAssert.Equal(value, JsonNode.Parse(request.ToJson()));
        }

        [Fact]
        public void Spec_response_example_parses()
        {
            var example = Repo.Spec["paths"]!["/api/comm/v1/send/omni"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]!["example"]!;

            var response = JsonSerializer.Deserialize(example.ToJsonString(), BizgoJsonContext.Default.SendOmniResponse)!;

            Assert.Equal("A000", response.Common!.AuthCode);
            Assert.Equal("20260424104234546POM101182450000", response.Data!.Data!.Destinations!.Single().MsgKey);
        }

        [Fact]
        public void Brand_upload_paths_match_the_spec()
        {
            var specPaths = Repo.Spec["paths"]!.AsObject().Select(p => p.Key).Where(p => p.StartsWith("/api/comm/v1/file/brandmessage/", StringComparison.Ordinal) && !p.Contains("/catalog", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal);
            var sdkPaths = ((BrandImageKind[])Enum.GetValues(typeof(BrandImageKind))).Select(k => "/api/comm/v1/file/brandmessage/" + FilesResource.KindPath(k)).OrderBy(p => p, StringComparer.Ordinal);
            Assert.Equal(specPaths, sdkPaths);
        }

        [Fact]
        public void Service_code_table_is_generated_from_error_codes()
        {
            var json = JsonNode.Parse(Repo.Read("spec", "error-codes.json"))!;
            var service = json["service"]!.AsArray();
            Assert.NotEmpty(service);
            foreach (var entry in service)
            {
                Assert.True(ServiceCodes.TryGet((string)entry!["code"]!, out _));
            }

            Assert.True(ServiceCodes.TryGet("A306", out var a306));
            Assert.Equal(400, a306.HttpStatus);
        }

        [Fact]
        public void Request_models_reject_unknown_members_and_responses_keep_them()
        {
            // request-only JSON models; multipart forms (FileUpload properties) are not JSON
            var models = typeof(SendOmniRequest).Assembly.GetExportedTypes().Where(t => t.Namespace == "Bizgo.Models" && t.IsClass && t.GetProperty("AdditionalProperties") is null && t.GetMethod("Validate", Type.EmptyTypes) is not null && !t.GetProperties().Any(p => p.PropertyType == typeof(FileUpload) || p.PropertyType == typeof(IList<FileUpload>))).ToArray();
            Assert.Contains(typeof(SmsMessage), models);
            foreach (var type in models)
            {
                var attribute = type.GetCustomAttribute<System.Text.Json.Serialization.JsonUnmappedMemberHandlingAttribute>();
                Assert.NotNull(attribute);
            }
        }
    }
}
