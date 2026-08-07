using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AggregationEngine.ScalabilityStudy
{
    // Emits, for a synthetic aggregate of N topic kinds, the source a
    // developer would have to write under each approach:
    //
    //   - the callback/dictionary approach, mirroring the structure of the
    //     hand-written LegacyAggregator in AggregationEngine.Benchmarks
    //     (one store per kind, one receive callback per kind, one lookup
    //     block per kind inside the completeness check, one field per kind
    //     in the result type, plus a reverse index for 0..* kinds), and
    //   - the proposed engine, where the same model is a list of
    //     RegisterKind / Register(Uni|Bi)directional calls.
    //
    // Topic class definitions are emitted separately and excluded from the
    // comparison: both approaches need them and their cost is identical, so
    // including them would dilute the difference being measured rather than
    // describe it.
    //
    // The generators follow the same rules at every N, so the per-kind
    // marginal cost is measured rather than assumed - and the emitted code
    // is compiled and executed (see Verifier) so the line counts describe
    // working implementations, not sketches.
    public enum PartShape { One, ZeroOrOne, ZeroOrMany }

    public sealed class Model
    {
        public int PartCount { get; }
        public IReadOnlyList<PartShape> Shapes { get; }

        public Model(int partCount)
        {
            PartCount = partCount;
            // Rotate the three shapes so every N contains a representative
            // mix rather than only the cheapest case.
            var shapes = new List<PartShape>();
            for (int i = 0; i < partCount; i++)
                shapes.Add((PartShape)(i % 3));
            Shapes = shapes;
        }

        public string PartName(int i) => $"C_Part{i:D2}";
        public string FieldName(int i) => Shapes[i] switch
        {
            PartShape.ZeroOrMany => $"A_part{i:D2}s_sourceID",
            _ => $"A_part{i:D2}_sourceID",
        };
        // Parts that reference the root back are registered bidirectionally.
        public bool IsReciprocal(int i) => Shapes[i] != PartShape.One;
    }

    public static class ModelGenerator
    {
        public const string RootName = "C_Root";

        // ---- shared: topic class definitions (excluded from the counts) --
        public static string Topics(Model m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace Generated");
            sb.AppendLine("{");
            sb.AppendLine($"    public sealed class {RootName}");
            sb.AppendLine("    {");
            sb.AppendLine("        public long A_sourceID;");
            for (int i = 0; i < m.PartCount; i++)
            {
                sb.AppendLine(m.Shapes[i] switch
                {
                    PartShape.One => $"        public long {m.FieldName(i)};",
                    PartShape.ZeroOrOne => $"        public long? {m.FieldName(i)};",
                    _ => $"        public long[] {m.FieldName(i)} = Array.Empty<long>();",
                });
            }
            sb.AppendLine("    }");
            for (int i = 0; i < m.PartCount; i++)
            {
                sb.AppendLine();
                sb.AppendLine($"    public sealed class {m.PartName(i)}");
                sb.AppendLine("    {");
                sb.AppendLine("        public long A_sourceID;");
                if (m.IsReciprocal(i))
                    sb.AppendLine("        public long A_root_sourceID;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ---- approach A: hand-written callbacks + dictionaries -----------
        public static string Legacy(Model m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class LegacyAggregate");
            sb.AppendLine("    {");
            sb.AppendLine($"        public {RootName}? Root;");
            for (int i = 0; i < m.PartCount; i++)
            {
                sb.AppendLine(m.Shapes[i] == PartShape.ZeroOrMany
                    ? $"        public List<{m.PartName(i)}> {m.PartName(i)}s = new List<{m.PartName(i)}>();"
                    : $"        public {m.PartName(i)}? {m.PartName(i)}Value;");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public sealed class LegacyAggregator");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly Dictionary<long, {RootName}> _roots = new Dictionary<long, {RootName}>();");
            for (int i = 0; i < m.PartCount; i++)
                sb.AppendLine($"        private readonly Dictionary<long, {m.PartName(i)}> _p{i:D2} = new Dictionary<long, {m.PartName(i)}>();");
            for (int i = 0; i < m.PartCount; i++)
                if (m.Shapes[i] == PartShape.ZeroOrMany)
                    sb.AppendLine($"        private readonly Dictionary<long, List<{m.PartName(i)}>> _p{i:D2}ByRoot = new Dictionary<long, List<{m.PartName(i)}>>();");
            sb.AppendLine();
            sb.AppendLine("        public int CompletedCount;");
            sb.AppendLine("        public event Action<LegacyAggregate>? OnComplete;");
            sb.AppendLine();
            sb.AppendLine($"        public void OnRoot({RootName} r)");
            sb.AppendLine("        {");
            sb.AppendLine("            _roots[r.A_sourceID] = r;");
            sb.AppendLine("            TryComplete(r.A_sourceID);");
            sb.AppendLine("        }");
            for (int i = 0; i < m.PartCount; i++)
            {
                sb.AppendLine();
                sb.AppendLine($"        public void OnPart{i:D2}({m.PartName(i)} p)");
                sb.AppendLine("        {");
                sb.AppendLine($"            _p{i:D2}[p.A_sourceID] = p;");
                if (m.Shapes[i] == PartShape.ZeroOrMany)
                {
                    sb.AppendLine($"            if (!_p{i:D2}ByRoot.TryGetValue(p.A_root_sourceID, out var list))");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                list = new List<{m.PartName(i)}>();");
                    sb.AppendLine($"                _p{i:D2}ByRoot[p.A_root_sourceID] = list;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            list.Add(p);");
                    sb.AppendLine("            TryComplete(p.A_root_sourceID);");
                }
                else if (m.IsReciprocal(i))
                {
                    sb.AppendLine("            TryComplete(p.A_root_sourceID);");
                }
                else
                {
                    sb.AppendLine("            // no back-reference on this kind; re-checked when the root arrives");
                }
                sb.AppendLine("        }");
            }
            sb.AppendLine();
            sb.AppendLine("        private void TryComplete(long rootId)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!_roots.TryGetValue(rootId, out var root)) return;");
            sb.AppendLine("            var result = new LegacyAggregate { Root = root };");
            for (int i = 0; i < m.PartCount; i++)
            {
                switch (m.Shapes[i])
                {
                    case PartShape.One:
                        sb.AppendLine($"            if (!_p{i:D2}.TryGetValue(root.{m.FieldName(i)}, out var v{i:D2})) return;");
                        sb.AppendLine($"            result.{m.PartName(i)}Value = v{i:D2};");
                        break;
                    case PartShape.ZeroOrOne:
                        sb.AppendLine($"            if (root.{m.FieldName(i)}.HasValue)");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                if (!_p{i:D2}.TryGetValue(root.{m.FieldName(i)}.Value, out var v{i:D2})) return;");
                        sb.AppendLine($"                result.{m.PartName(i)}Value = v{i:D2};");
                        sb.AppendLine("            }");
                        break;
                    default:
                        sb.AppendLine($"            foreach (var id{i:D2} in root.{m.FieldName(i)})");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                if (!_p{i:D2}.TryGetValue(id{i:D2}, out var v{i:D2})) return;");
                        sb.AppendLine($"                result.{m.PartName(i)}s.Add(v{i:D2});");
                        sb.AppendLine("            }");
                        break;
                }
            }
            sb.AppendLine("            CompletedCount++;");
            sb.AppendLine("            OnComplete?.Invoke(result);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ---- approach B: declarative registration on the engine ----------
        public static string Engine(Model m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using TopicManager.Extensions;");
            sb.AppendLine();
            sb.AppendLine("namespace Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class EngineSetup");
            sb.AppendLine("    {");
            sb.AppendLine("        public static KindId Build(global::TopicManager.Extensions.AggregationEngine e, out KindId[] partKinds)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var root = e.RegisterKind<{RootName}>(x => x.A_sourceID);");
            sb.AppendLine($"            partKinds = new KindId[{m.PartCount}];");
            for (int i = 0; i < m.PartCount; i++)
                sb.AppendLine($"            partKinds[{i}] = e.RegisterKind<{m.PartName(i)}>(x => x.A_sourceID);");
            sb.AppendLine("            e.RegisterRootKind(root);");
            for (int i = 0; i < m.PartCount; i++)
            {
                var part = m.PartName(i);
                var field = m.FieldName(i);
                switch (m.Shapes[i])
                {
                    case PartShape.One:
                        sb.AppendLine($"            e.RegisterUnidirectional<{RootName}, {part}>(\"r-p{i:D2}\", root, partKinds[{i}], Multiplicity.One,");
                        sb.AppendLine($"                x => global::TopicManager.Extensions.AggregationEngine.One(x.{field}));");
                        break;
                    case PartShape.ZeroOrOne:
                        sb.AppendLine($"            e.RegisterBidirectional<{RootName}, {part}>(\"r-p{i:D2}\", root, partKinds[{i}], Multiplicity.ZeroOrOne, Multiplicity.One,");
                        sb.AppendLine($"                x => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(x.{field}.HasValue, x.{field}.GetValueOrDefault()),");
                        sb.AppendLine($"                y => global::TopicManager.Extensions.AggregationEngine.One(y.A_root_sourceID));");
                        break;
                    default:
                        sb.AppendLine($"            e.RegisterBidirectional<{RootName}, {part}>(\"r-p{i:D2}\", root, partKinds[{i}], Multiplicity.ZeroOrMany, Multiplicity.One,");
                        sb.AppendLine($"                x => global::TopicManager.Extensions.AggregationEngine.Many(x.{field}),");
                        sb.AppendLine($"                y => global::TopicManager.Extensions.AggregationEngine.One(y.A_root_sourceID));");
                        break;
                }
            }
            sb.AppendLine("            return root;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Lines that actually carry model knowledge: blank lines, braces
        /// and comments are excluded so the count reflects work a developer
        /// does rather than formatting.
        /// </summary>
        public static int SignificantLines(string source) =>
            source.Split('\n')
                  .Select(l => l.Trim())
                  .Count(l => l.Length > 0 && l != "{" && l != "}" && !l.StartsWith("//"));

        /// <summary>
        /// Distinct code sites a developer must edit to add one more topic
        /// kind. Counted from the generator's own structure, which is what
        /// the emitted source reflects.
        /// </summary>
        public static (int legacy, int engine) ChangePointsPerKind(PartShape shape) =>
            // legacy: store field, receive callback, TryComplete block,
            //         result-type field, and for 0..* a reverse index too
            (shape == PartShape.ZeroOrMany ? 5 : 4,
            // engine: one RegisterKind + one relation registration, both in
            //         the same registration block
             1);
    }
}
