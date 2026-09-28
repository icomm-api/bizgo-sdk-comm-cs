using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Bizgo.Codegen
{
    /// <summary>
    /// <c>x-sdk-required-if</c> (bizgo-api-spec AGENTS.md rule 11, SDK-DESIGN.md §4): conditionally required fields.
    /// <list type="bullet">
    /// <item><description>Each rule becomes <c>if (condition) { ... }</c> at the end of the model's <c>ValidateInto</c>; rules are AND-ed.</description></item>
    /// <item><description><c>when.field</c> is compared as a string; a missing field makes <c>equals</c>/<c>in</c> false and <c>notEquals</c>/<c>notIn</c> true.</description></item>
    /// <item><description><c>required</c>: own properties that must be non-null. <c>requiredPaths</c>: dotted paths, <c>name[]</c> = every element
    /// (a missing or empty array has no elements), a missing intermediate object means the path is missing.</description></item>
    /// <item><description><c>$.</c> paths start at the request body (<c>RequestValidator.Root</c>, set by the body model's <c>ValidateInto</c>);
    /// one typed branch per request body schema that has the first property. Validating a nested model alone skips them.</description></item>
    /// <item><description>Error reasons name the condition (field path, operator, the spec's literal) and never the input values.</description></item>
    /// </list>
    /// </summary>
    public static partial class Generator
    {
        private static readonly string[] Operators = { "equals", "notEquals", "in", "notIn" };

        private static void EmitRequiredIf(CodeWriter w, ClassDef cls, SpecModel model)
        {
            if (cls.RequiredIf is null)
            {
                return;
            }

            if (cls.IsFlowUnion)
            {
                throw new InvalidOperationException(cls.Name + ": x-sdk-required-if on a oneOf schema is not supported");
            }

            foreach (var ruleNode in cls.RequiredIf)
            {
                var rule = ruleNode as JsonObject ?? throw new InvalidOperationException(cls.Name + ": x-sdk-required-if rule must be an object");
                foreach (var key in rule.Select(e => e.Key))
                {
                    if (key != "when" && key != "required" && key != "requiredPaths")
                    {
                        throw new InvalidOperationException(cls.Name + ": unknown x-sdk-required-if key " + key);
                    }
                }

                var when = rule["when"] as JsonObject ?? throw new InvalidOperationException(cls.Name + ": x-sdk-required-if rule without when");
                var fieldName = Str(when, "field") ?? throw new InvalidOperationException(cls.Name + ": x-sdk-required-if when.field missing");
                var field = cls.Props.FirstOrDefault(p => p.ApiName == fieldName) ?? throw new InvalidOperationException(cls.Name + ": x-sdk-required-if when.field " + fieldName + " is not a property");
                var ops = when.Select(e => e.Key).Where(k => k != "field").ToList();
                if (ops.Count != 1 || Array.IndexOf(Operators, ops[0]) < 0)
                {
                    throw new InvalidOperationException(cls.Name + ": x-sdk-required-if when needs exactly one of " + string.Join(", ", Operators));
                }

                var op = ops[0];
                var values = op == "in" || op == "notIn"
                    ? (when[op] as JsonArray ?? throw new InvalidOperationException(cls.Name + ": " + op + " needs a list")).Select(Scalar).ToList()
                    : new List<string?> { Scalar(when[op]) };
                if (values.Count == 0 || values.Any(x => x is null))
                {
                    throw new InvalidOperationException(cls.Name + ": x-sdk-required-if values must be scalars");
                }

                var required = Strings(rule, "required") ?? new List<string>();
                var paths = Strings(rule, "requiredPaths") ?? new List<string>();
                if (required.Count == 0 && paths.Count == 0)
                {
                    throw new InvalidOperationException(cls.Name + ": x-sdk-required-if rule needs required or requiredPaths");
                }

                var shownValues = string.Join(", ", values);
                var condition = op switch
                {
                    "equals" => " == " + shownValues,
                    "notEquals" => " != " + shownValues,
                    "in" => " in (" + shownValues + ")",
                    _ => " not in (" + shownValues + ")",
                } + "일 때 필수입니다";

                w.Line();
                w.Line("if (" + ConditionCode(field, op, values!) + ")");
                w.Open();
                foreach (var name in required)
                {
                    var prop = cls.Props.FirstOrDefault(p => p.ApiName == name) ?? throw new InvalidOperationException(cls.Name + ": x-sdk-required-if required " + name + " is not a property");
                    w.Line("v.RequiredWhen(path, " + Literal(name) + ", " + prop.CsName + ", " + Literal(fieldName + condition) + ");");
                }

                // Reasons of path checks name the condition field by its full path: the error can be far from it ("destinations[0]...").
                var reason = "RequestValidator.Child(path, " + Literal(fieldName) + ") + " + Literal(condition);
                foreach (var path in paths)
                {
                    // one block per path: the walk's locals (l0, e0, ...) do not clash
                    w.Open();
                    if (path.StartsWith("$.", StringComparison.Ordinal))
                    {
                        var segments = Segments(cls, path.Substring(2));
                        var roots = model.Classes.Where(c => c.IsBodyRoot && Member(c, Bare(segments[0])) is not null).ToList();
                        if (roots.Count == 0)
                        {
                            throw new InvalidOperationException(cls.Name + ": no request body schema has the first property of " + path);
                        }

                        for (var r = 0; r < roots.Count; r++)
                        {
                            var root = "r" + r.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            w.Line("if (v.Root is " + roots[r].Name + " " + root + ")");
                            w.Open();
                            EmitWalk(w, model, roots[r], root, "string.Empty", string.Empty, segments, 0, reason);
                            w.Close();
                        }
                    }
                    else
                    {
                        EmitWalk(w, model, cls, null, "path", string.Empty, Segments(cls, path), 0, reason);
                    }

                    w.Close();
                }

                w.Close();
            }
        }

        private static string ConditionCode(PropDef field, string op, List<string> values)
        {
            string value;
            if (field.Type == "string")
            {
                value = field.CsName;
            }
            else if (field.IsValueType)
            {
                value = "RequestValidator.Text(" + field.CsName + ")";
            }
            else
            {
                throw new InvalidOperationException(field.ApiName + ": x-sdk-required-if when.field must be a string, number or boolean");
            }

            var literals = string.Join(", ", values.Select(Literal));
            return op switch
            {
                "equals" => "RequestValidator.In(" + value + ", " + literals + ")",
                "notEquals" => "!RequestValidator.In(" + value + ", " + literals + ")",
                "in" => "RequestValidator.In(" + value + ", " + literals + ")",
                _ => "!RequestValidator.In(" + value + ", " + literals + ")",
            };
        }

        private static string[] Segments(ClassDef cls, string path)
        {
            var segments = path.Split('.');
            if (segments.Any(s => s.Length == 0 || Bare(s).Length == 0 || Bare(s).Contains('[', StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(cls.Name + ": invalid x-sdk-required-if path " + path);
            }

            return segments;
        }

        private static string Bare(string segment) => segment.EndsWith("[]", StringComparison.Ordinal) ? segment.Substring(0, segment.Length - 2) : segment;

        /// <summary>(C# property, class of the value or of the list's elements when it is a model, is a list) of a property or messageFlow channel key.</summary>
        private static (string CsName, string? Class, bool IsList)? Member(ClassDef cls, string name)
        {
            if (cls.IsFlowUnion)
            {
                var member = cls.FlowMembers.FirstOrDefault(m => m.Key == name);
                return member.Key is null ? null : (FlowProperty(member.Key), member.Value, false);
            }

            var prop = cls.Props.FirstOrDefault(p => p.ApiName == name);
            if (prop is null)
            {
                return null;
            }

            return prop.IsList ? (prop.CsName, prop.ItemClass, true) : (prop.CsName, prop.ClassRef, false);
        }

        /// <summary>
        /// Emits the check of <paramref name="segments"/>[<paramref name="k"/>..] on the object <paramref name="expr"/> (never null; null = <c>this</c>),
        /// whose path is <paramref name="pathExpr"/> (C# expression) followed by the dotted <paramref name="suffix"/>.
        /// </summary>
        private static void EmitWalk(CodeWriter w, SpecModel model, ClassDef cls, string? expr, string pathExpr, string suffix, string[] segments, int k, string reason)
        {
            var segment = segments[k];
            var name = Bare(segment);
            var isArray = segment.Length != name.Length;
            var last = k == segments.Length - 1;
            var member = Member(cls, name) ?? throw new InvalidOperationException(cls.Name + ": x-sdk-required-if path property " + name + " does not exist");
            if (isArray != member.IsList)
            {
                throw new InvalidOperationException(cls.Name + "." + name + ": x-sdk-required-if path must use [] exactly for arrays");
            }

            if (!last && member.Class is null)
            {
                throw new InvalidOperationException(cls.Name + "." + name + ": x-sdk-required-if path goes through a property that is not a model");
            }

            var depth = k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rest = last ? string.Empty : "." + string.Join(".", segments.Skip(k + 1));
            var access = expr is null ? member.CsName : expr + "." + member.CsName;
            var here = suffix.Length == 0 ? name : suffix + "." + name;
            if (!isArray)
            {
                if (last)
                {
                    w.Line("if (" + access + " is null)");
                    w.Open();
                    w.Line("v.Add(RequestValidator.Child(" + pathExpr + ", " + Literal(here) + "), " + reason + ");");
                    w.Close();
                    return;
                }

                var local = "e" + depth;
                w.Line("var " + local + " = " + access + ";");
                w.Line("if (" + local + " is null)");
                w.Open();
                w.Line("v.Add(RequestValidator.Child(" + pathExpr + ", " + Literal(here + rest) + "), " + reason + ");");
                w.Close();
                w.Line("else");
                w.Open();
                EmitWalk(w, model, model.Find(member.Class!), local, pathExpr, here, segments, k + 1, reason);
                w.Close();
                return;
            }

            var list = "l" + depth;
            var index = "i" + depth;
            var element = "e" + depth;
            var at = "p" + depth;
            w.Line("var " + list + " = " + access + ";");
            w.Line("for (var " + index + " = 0; " + list + " is not null && " + index + " < " + list + ".Count; " + index + "++)");
            w.Open();
            w.Line("var " + at + " = RequestValidator.Index(" + pathExpr + ", " + Literal(here) + ", " + index + ");");
            w.Line("var " + element + " = " + list + "[" + index + "];");
            w.Line("if (" + element + " is null)");
            w.Open();
            w.Line("v.Add(" + at + (last ? string.Empty : " + " + Literal(rest)) + ", " + reason + ");");
            w.Close();
            if (!last)
            {
                w.Line("else");
                w.Open();
                EmitWalk(w, model, model.Find(member.Class!), element, at, string.Empty, segments, k + 1, reason);
                w.Close();
            }

            w.Close();
        }
    }
}
