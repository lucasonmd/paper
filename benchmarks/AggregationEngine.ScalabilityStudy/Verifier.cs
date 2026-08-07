using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AggregationEngine.ScalabilityStudy
{
    // Compiles the generated sources and runs both implementations over the
    // same input, so the line counts reported alongside describe code that
    // actually works. Without this the comparison would only be counting
    // text.
    public static class Verifier
    {
        public sealed class Result
        {
            public bool Compiled;
            public string? CompileError;
            public int LegacyCompletions;
            public int EngineCompletions;
            public bool Agrees => Compiled && LegacyCompletions == EngineCompletions && LegacyCompletions > 0;
        }

        public static Result CompileAndRun(Model m)
        {
            var res = new Result();

            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(ModelGenerator.Topics(m)),
                CSharpSyntaxTree.ParseText(ModelGenerator.Legacy(m)),
                CSharpSyntaxTree.ParseText(ModelGenerator.Engine(m)),
            };

            // Touch a type from the engine so its assembly is loaded before
            // the AppDomain is enumerated - it is lazily loaded otherwise
            // and would be missing from the reference set.
            var engineAsm = typeof(TopicManager.Extensions.AggregationEngine).Assembly;

            var locations = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location)
                .Append(engineAsm.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var refs = locations
                .Select(loc => (MetadataReference)MetadataReference.CreateFromFile(loc))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "Generated_" + m.PartCount,
                trees,
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                res.CompileError = string.Join("; ", emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(3).Select(d => d.ToString()));
                return res;
            }
            res.Compiled = true;

            ms.Seek(0, SeekOrigin.Begin);
            var asm = Assembly.Load(ms.ToArray());
            res.LegacyCompletions = RunLegacy(asm, m);
            res.EngineCompletions = RunEngine(asm, m);
            return res;
        }

        private const int Aggregates = 50;

        // Builds the same population of aggregates for both sides. Ids are
        // laid out so that root r owns part ids r*1000 + i (+ a second
        // element for 0..* kinds).
        private static IEnumerable<(int root, int part, long partId, bool second)> Plan(Model m)
        {
            for (int r = 1; r <= Aggregates; r++)
                for (int i = 0; i < m.PartCount; i++)
                {
                    yield return (r, i, r * 1000L + i, false);
                    if (m.Shapes[i] == PartShape.ZeroOrMany)
                        yield return (r, i, r * 1000L + 500 + i, true);
                }
        }

        private static int RunLegacy(Assembly asm, Model m)
        {
            var aggType = asm.GetType("Generated.LegacyAggregator")!;
            var agg = Activator.CreateInstance(aggType)!;
            var rootType = asm.GetType("Generated." + ModelGenerator.RootName)!;

            foreach (var (r, i, partId, _) in Plan(m))
            {
                var partType = asm.GetType("Generated." + m.PartName(i))!;
                var p = Activator.CreateInstance(partType)!;
                partType.GetField("A_sourceID")!.SetValue(p, partId);
                var back = partType.GetField("A_root_sourceID");
                back?.SetValue(p, (long)r);
                aggType.GetMethod($"OnPart{i:D2}")!.Invoke(agg, new[] { p });
            }

            for (int r = 1; r <= Aggregates; r++)
                aggType.GetMethod("OnRoot")!.Invoke(agg, new[] { BuildRoot(rootType, m, r) });

            return (int)aggType.GetField("CompletedCount")!.GetValue(agg)!;
        }

        private static int RunEngine(Assembly asm, Model m)
        {
            var engine = new TopicManager.Extensions.AggregationEngine();
            var setup = asm.GetType("Generated.EngineSetup")!;
            var args = new object?[] { engine, null };
            var rootKind = (TopicManager.Extensions.KindId)setup.GetMethod("Build")!.Invoke(null, args)!;
            var partKinds = (TopicManager.Extensions.KindId[])args[1]!;

            int completed = 0;
            Action<TopicManager.Extensions.RootId, TopicManager.Extensions.AggregateSnapshot> h =
                (_, __) => completed++;
            engine.SubscribeRootKind(rootKind, h);

            var rootType = asm.GetType("Generated." + ModelGenerator.RootName)!;
            var upsert = typeof(TopicManager.Extensions.AggregationEngine).GetMethod("Upsert")!;

            foreach (var (r, i, partId, _) in Plan(m))
            {
                var partType = asm.GetType("Generated." + m.PartName(i))!;
                var p = Activator.CreateInstance(partType)!;
                partType.GetField("A_sourceID")!.SetValue(p, partId);
                partType.GetField("A_root_sourceID")?.SetValue(p, (long)r);
                upsert.MakeGenericMethod(partType).Invoke(engine, new object[] { partKinds[i], p });
            }

            for (int r = 1; r <= Aggregates; r++)
            {
                var root = BuildRoot(rootType, m, r);
                upsert.MakeGenericMethod(rootType).Invoke(engine, new object[] { rootKind, root });
            }

            return completed;
        }

        private static object BuildRoot(Type rootType, Model m, int r)
        {
            var root = Activator.CreateInstance(rootType)!;
            rootType.GetField("A_sourceID")!.SetValue(root, (long)r);
            for (int i = 0; i < m.PartCount; i++)
            {
                var f = rootType.GetField(m.FieldName(i))!;
                switch (m.Shapes[i])
                {
                    case PartShape.One:
                        f.SetValue(root, r * 1000L + i);
                        break;
                    case PartShape.ZeroOrOne:
                        f.SetValue(root, (long?)(r * 1000L + i));
                        break;
                    default:
                        f.SetValue(root, new[] { r * 1000L + i, r * 1000L + 500 + i });
                        break;
                }
            }
            return root;
        }
    }
}
