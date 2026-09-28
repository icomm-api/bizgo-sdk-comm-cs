using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bizgo.Codegen
{
    /// <summary>
    /// <c>dotnet run --project tools/Bizgo.Codegen</c> regenerates the models;
    /// <c>dotnet run --project tools/Bizgo.Codegen -- --check</c> fails (exit 1) when they are stale.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var check = args.Contains("--check");
            var root = FindRoot(Directory.GetCurrentDirectory()) ?? FindRoot(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("repository root (with spec/openapi.yaml) not found");
            var files = Generator.Generate(
                File.ReadAllText(Path.Combine(root, "spec", "openapi.yaml"), Encoding.UTF8),
                File.ReadAllText(Path.Combine(root, "spec", "error-codes.json"), Encoding.UTF8));

            var outputDir = Path.Combine(root, Generator.OutputDirectory);
            var expected = new HashSet<string>(files.Select(f => Normalize(Path.Combine(root, f.Path))), StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();

            foreach (var file in files)
            {
                var path = Path.Combine(root, file.Path);
                var current = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
                if (current == file.Content)
                {
                    continue;
                }

                if (check)
                {
                    problems.Add((current == null ? "missing: " : "stale:   ") + file.Path + FirstDifference(current, file.Content));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content, new UTF8Encoding(false));
                Console.WriteLine("wrote " + file.Path);
            }

            if (Directory.Exists(outputDir))
            {
                foreach (var existing in Directory.GetFiles(outputDir, "*.g.cs", SearchOption.AllDirectories))
                {
                    if (expected.Contains(Normalize(existing)))
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(root, existing).Replace(Path.DirectorySeparatorChar, '/');
                    if (check)
                    {
                        problems.Add("extra:   " + relative);
                    }
                    else
                    {
                        File.Delete(existing);
                        Console.WriteLine("deleted " + relative);
                    }
                }
            }

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    Console.Error.WriteLine(problem);
                }

                Console.Error.WriteLine("generated code is out of date: run `dotnet run --project tools/Bizgo.Codegen` and commit the result");
                return 1;
            }

            if (check)
            {
                Console.WriteLine("generated code is up to date (" + files.Count + " files)");
            }

            return 0;
        }

        private static string Normalize(string path) => Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/');

        private static string? FindRoot(string start)
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "spec", "openapi.yaml")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                {
                    return dir.FullName;
                }
            }

            return null;
        }

        private static string FirstDifference(string? current, string expected)
        {
            if (current == null)
            {
                return string.Empty;
            }

            var a = current.Split('\n');
            var b = expected.Split('\n');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var left = i < a.Length ? a[i] : "<eof>";
                var right = i < b.Length ? b[i] : "<eof>";
                if (left != right)
                {
                    return "\n    line " + (i + 1) + "\n    - " + left + "\n    + " + right;
                }
            }

            return string.Empty;
        }
    }
}
