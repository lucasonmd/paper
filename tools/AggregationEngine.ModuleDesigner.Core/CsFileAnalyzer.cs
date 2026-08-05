using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AggregationEngine.ModuleDesigner.Core
{
    // Parses .cs source files with Roslyn's SYNTAX layer only - no
    // compilation, no need for the files' own dependencies (other IDL
    // packages, DDS runtime types, etc.) to be resolvable. This is
    // deliberate: the whole point is "drop in whatever .cs files you have,
    // even without a buildable solution around them" (see the conversation
    // this was designed in). Semantic analysis would give more precise
    // type resolution but would require a full Compilation with every
    // referenced assembly present, which defeats that goal.
    public static class CsFileAnalyzer
    {
        // A_sourceID itself is the entity's own key, not a relation field.
        private const string KeyFieldName = "A_sourceID";

        // Matches "A_<target>_sourceID" - a candidate foreign-key field.
        // Deliberately excludes a bare "A_sourceID" match (RegexOptions
        // requires at least one character in the middle group).
        private static readonly Regex RelationFieldPattern =
            new(@"^A_(.+)_sourceID$", RegexOptions.Compiled);

        public static List<DetectedTopic> Analyze(IEnumerable<string> filePaths)
        {
            var topics = new List<DetectedTopic>();

            foreach (var path in filePaths)
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception ex) { throw new InvalidOperationException($"Could not read '{path}': {ex.Message}", ex); }

                var tree = CSharpSyntaxTree.ParseText(text, path: path);
                var root = tree.GetCompilationUnitRoot();

                foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var topic = AnalyzeClass(classDecl, path);
                    if (topic != null) topics.Add(topic);
                }

                // Structs matter too - NGVA's own T_IdentifierType-shaped
                // composite key types are structs, and in principle a
                // DDS-generated "topic" could be declared as a struct in
                // some toolchains, so the same detection applies.
                foreach (var structDecl in root.DescendantNodes().OfType<StructDeclarationSyntax>())
                {
                    var topic = AnalyzeTypeDeclaration(structDecl, structDecl.Identifier.Text, path);
                    if (topic != null) topics.Add(topic);
                }
            }

            return topics;
        }

        private static DetectedTopic? AnalyzeClass(ClassDeclarationSyntax classDecl, string path) =>
            AnalyzeTypeDeclaration(classDecl, classDecl.Identifier.Text, path);

        // NGVA's own naming convention: entity/topic classes are prefixed
        // "C_" (C_Rotational_Mount, C_Actual_Mount, ...), auxiliary/value
        // types are not (T_IdentifierType, ...). This is the class-level
        // gate for "is this a Topic at all" - deliberately independent of
        // whether a specific key field name is present, since that can
        // legitimately vary (see KeyField fallback below).
        private const string TopicClassPrefix = "C_";

        private static DetectedTopic? AnalyzeTypeDeclaration(TypeDeclarationSyntax typeDecl, string className, string path)
        {
            if (!className.StartsWith(TopicClassPrefix, StringComparison.Ordinal))
                return null; // not a Topic candidate by the C_ naming convention

            var members = CollectPublicMembers(typeDecl);
            var hasKey = members.Any(m => m.FieldName == KeyFieldName);

            var topic = new DetectedTopic
            {
                ClassName = className,
                Namespace = GetNamespace(typeDecl),
                SourceFile = path,
                // Best-effort: default to the A_sourceID convention when
                // present, otherwise leave blank so the (editable) Key
                // field column in the review UI visibly flags that this
                // Topic needs the user to fill it in by hand.
                KeyField = hasKey ? KeyFieldName : "",
            };

            foreach (var m in members)
            {
                if (m.FieldName == KeyFieldName) continue;
                if (!RelationFieldPattern.IsMatch(m.FieldName)) continue;
                topic.CandidateFields.Add(m);
            }

            return topic;
        }

        // Public field or property declarations directly on this type
        // (not inherited, not private/protected helpers) - both are valid
        // shapes for DDS-generated members depending on the vendor's
        // IDL-to-C# toolchain.
        private static List<DetectedField> CollectPublicMembers(TypeDeclarationSyntax typeDecl)
        {
            var result = new List<DetectedField>();

            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field when IsPublic(field.Modifiers):
                        var typeText = field.Declaration.Type.ToString();
                        foreach (var declarator in field.Declaration.Variables)
                        {
                            result.Add(new DetectedField
                            {
                                FieldName = declarator.Identifier.Text,
                                TypeText = typeText,
                                Shape = ClassifyShape(typeText),
                            });
                        }
                        break;

                    case PropertyDeclarationSyntax prop when IsPublic(prop.Modifiers):
                        var propType = prop.Type.ToString();
                        result.Add(new DetectedField
                        {
                            FieldName = prop.Identifier.Text,
                            TypeText = propType,
                            Shape = ClassifyShape(propType),
                        });
                        break;
                }
            }

            return result;
        }

        private static bool IsPublic(SyntaxTokenList modifiers) =>
            modifiers.Any(SyntaxKind.PublicKeyword);

        private static FieldShape ClassifyShape(string typeText)
        {
            var t = typeText.Trim();

            if (t.EndsWith("[]", StringComparison.Ordinal))
                return FieldShape.Enumerable;

            if (Regex.IsMatch(t, @"^(System\.Collections\.Generic\.)?(List|IList|IReadOnlyList|ICollection|IEnumerable)\s*<", RegexOptions.IgnoreCase))
                return FieldShape.Enumerable;

            if (t.EndsWith("?", StringComparison.Ordinal))
                return FieldShape.Nullable;

            if (Regex.IsMatch(t, @"^(System\.)?Nullable\s*<", RegexOptions.IgnoreCase))
                return FieldShape.Nullable;

            return FieldShape.Scalar;
        }

        private static string? GetNamespace(SyntaxNode node)
        {
            for (var cur = node.Parent; cur != null; cur = cur.Parent)
            {
                switch (cur)
                {
                    case NamespaceDeclarationSyntax ns:
                        return ns.Name.ToString();
                    case FileScopedNamespaceDeclarationSyntax fns:
                        return fns.Name.ToString();
                }
            }
            return null;
        }
    }
}
