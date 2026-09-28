using System;
using System.IO;
using System.Linq;
using System.Text;
using Bizgo.Codegen;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>Fails when src/Bizgo.Sdk.Comm/Generated is not what the generator produces from spec/ (run the generator and commit).</summary>
    public class GeneratedCodeTests
    {
        [Fact]
        public void Generated_code_matches_the_spec()
        {
            var root = Repo.Root;
            var files = Generator.Generate(Repo.Read("spec", "openapi.yaml"), Repo.Read("spec", "error-codes.json"));

            var stale = files
                .Where(f =>
                {
                    var path = Path.Combine(root, f.Path);
                    return !File.Exists(path) || File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal) != f.Content;
                })
                .Select(f => f.Path)
                .ToList();
            var expected = files.Select(f => Path.GetFullPath(Path.Combine(root, f.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extra = Directory.GetFiles(Path.Combine(root, Generator.OutputDirectory), "*.g.cs", SearchOption.AllDirectories)
                .Where(p => !expected.Contains(Path.GetFullPath(p)))
                .ToList();

            Assert.True(stale.Count == 0 && extra.Count == 0, "run `dotnet run --project tools/Bizgo.Codegen`; stale: " + string.Join(", ", stale) + " extra: " + string.Join(", ", extra));
        }

        [Fact]
        public void Generation_is_deterministic()
        {
            var spec = Repo.Read("spec", "openapi.yaml");
            var codes = Repo.Read("spec", "error-codes.json");
            var first = Generator.Generate(spec, codes);
            var second = Generator.Generate(spec, codes);
            Assert.Equal(first.Select(f => f.Path + f.Content), second.Select(f => f.Path + f.Content));
        }

        [Fact]
        public void Generated_operation_whose_body_has_key_and_ttl_uses_the_default_helper()
        {
            // IdempotencyTtlTests: no generated operation has both fields today (send is hand-written); add them to reservation create to check the generator.
            var spec = Repo.Spec;
            var schemas = spec["components"]!["schemas"]!;
            var send = schemas["SendOmniRequest"]!["properties"]!;
            var reservation = schemas["ReservationCreateRequest"]!["properties"]!.AsObject();
            reservation["idempotencyKey"] = send["idempotencyKey"]!.DeepClone();
            reservation["idempotencyTtl"] = send["idempotencyTtl"]!.DeepClone();

            var files = Generator.Generate(spec.ToJsonString(), Repo.Read("spec", "error-codes.json"));

            var resource = files.Single(f => f.Path.EndsWith("ReservationsResource.g.cs", StringComparison.Ordinal)).Content;
            Assert.Contains("JsonBody.Create(request, BizgoJsonContext.Default.ReservationCreateRequest, request.IdempotencyKey, request.IdempotencyTtl)", resource, StringComparison.Ordinal);
            Assert.Contains("JsonBody.Create(request, BizgoJsonContext.Default.ReservationUpdateRequest);", resource, StringComparison.Ordinal);

            var committed = Repo.Read("src", "Bizgo.Sdk.Comm", "Generated", "Resources", "ReservationsResource.g.cs");
            Assert.Contains("JsonBody.Create(request, BizgoJsonContext.Default.ReservationCreateRequest);", committed, StringComparison.Ordinal);
        }
    }
}
