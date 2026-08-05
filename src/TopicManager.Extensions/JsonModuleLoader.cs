using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopicManager.Extensions
{
    // Reads a module schema (Kinds, RootKinds, Relations) from JSON and
    // drives it into an AggregationEngine via the reflection-based
    // RegisterKind/RegisterUnidirectional/RegisterBidirectional overloads.
    // One JSON file configures exactly one engine instance - this loader
    // does not merge or layer multiple modules into a shared Kind registry.
    //
    // This is the only piece that needs to be written per deployment; the
    // engine itself and the topic module's DDS-generated classes are
    // untouched. Adding a new module is "write/edit a JSON file", not
    // "write new C# registration code".
    public static class JsonModuleLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        // Returns the name -> KindId mapping the JSON declared, so the
        // caller (typically the code that wires up DDS DataReaders) can
        // look up which KindId to pass to engine.Upsert(...) for each
        // topic, without needing to know anything about how this loader
        // resolved types or in what order it registered them.
        public static IReadOnlyDictionary<string, KindId> LoadFile(AggregationEngine engine, string jsonFilePath) =>
            LoadJson(engine, File.ReadAllText(jsonFilePath));

        public static IReadOnlyDictionary<string, KindId> LoadJson(AggregationEngine engine, string json)
        {
            var schema = JsonSerializer.Deserialize<ModuleSchema>(json, JsonOptions)
                ?? throw new InvalidOperationException("Module JSON deserialized to null.");

            // name -> (KindId, CLR type), built as Kinds are registered so
            // Relations (processed afterward) can resolve both sides by name.
            var kinds = new Dictionary<string, (KindId Id, Type ClrType)>();

            foreach (var k in schema.Kinds)
            {
                if (string.IsNullOrEmpty(k.Name)) throw new InvalidOperationException("A kind entry is missing \"name\".");
                if (string.IsNullOrEmpty(k.ClrType)) throw new InvalidOperationException($"Kind \"{k.Name}\" is missing \"clrType\".");
                if (string.IsNullOrEmpty(k.KeyField)) throw new InvalidOperationException($"Kind \"{k.Name}\" is missing \"keyField\".");

                var clrType = ResolveType(k.ClrType);
                var kindId = engine.RegisterKind(clrType, k.KeyField);
                kinds[k.Name] = (kindId, clrType);
            }

            foreach (var rootName in schema.RootKinds)
            {
                engine.RegisterRootKind(Lookup(kinds, rootName, "rootKinds").Id);
            }

            foreach (var r in schema.Relations)
            {
                if (string.IsNullOrEmpty(r.Direction))
                    throw new InvalidOperationException($"Relation \"{r.Name}\" is missing \"direction\".");

                switch (r.Direction)
                {
                    case "unidirectional":
                        RegisterUnidirectionalFromSchema(engine, kinds, r);
                        break;
                    case "bidirectional":
                        RegisterBidirectionalFromSchema(engine, kinds, r);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Relation \"{r.Name}\": unknown direction \"{r.Direction}\" (expected \"unidirectional\" or \"bidirectional\").");
                }
            }

            return kinds.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
        }

        private static void RegisterUnidirectionalFromSchema(AggregationEngine engine, Dictionary<string, (KindId Id, Type ClrType)> kinds, RelationSchema r)
        {
            Require(r.From, r.Name, "from");
            Require(r.To, r.Name, "to");
            Require(r.FromField, r.Name, "fromField");
            Require(r.Multiplicity, r.Name, "multiplicity");

            var from = Lookup(kinds, r.From!, r.Name);
            var to = Lookup(kinds, r.To!, r.Name);
            var multiplicity = ParseMultiplicity(r.Multiplicity!, r.Name);
            var presence = multiplicity == Multiplicity.ZeroOrOne ? ParsePresenceCheck(r.PresenceCheck, r.Name) : PresenceCheck.Nullable;

            engine.RegisterUnidirectional(r.Name, from.Id, to.Id, multiplicity, from.ClrType, r.FromField!, presence);
        }

        private static void RegisterBidirectionalFromSchema(AggregationEngine engine, Dictionary<string, (KindId Id, Type ClrType)> kinds, RelationSchema r)
        {
            Require(r.Left, r.Name, "left");
            Require(r.Right, r.Name, "right");
            Require(r.LeftField, r.Name, "leftField");
            Require(r.RightField, r.Name, "rightField");
            Require(r.LeftToRightMultiplicity, r.Name, "leftToRightMultiplicity");
            Require(r.RightToLeftMultiplicity, r.Name, "rightToLeftMultiplicity");

            var left = Lookup(kinds, r.Left!, r.Name);
            var right = Lookup(kinds, r.Right!, r.Name);
            var leftToRight = ParseMultiplicity(r.LeftToRightMultiplicity!, r.Name);
            var rightToLeft = ParseMultiplicity(r.RightToLeftMultiplicity!, r.Name);

            // "presenceCheck" names whichever side is declared ZeroOrOne; a
            // relation with neither side ZeroOrOne simply ignores it.
            var leftPresence = leftToRight == Multiplicity.ZeroOrOne ? ParsePresenceCheck(r.PresenceCheck, r.Name) : PresenceCheck.Nullable;
            var rightPresence = rightToLeft == Multiplicity.ZeroOrOne ? ParsePresenceCheck(r.PresenceCheck, r.Name) : PresenceCheck.Nullable;

            engine.RegisterBidirectional(
                r.Name, left.Id, right.Id, leftToRight, rightToLeft,
                left.ClrType, r.LeftField!, right.ClrType, r.RightField!,
                leftPresence, rightPresence);
        }

        private static void Require(string? value, string relationName, string fieldName)
        {
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException($"Relation \"{relationName}\" is missing \"{fieldName}\".");
        }

        private static (KindId Id, Type ClrType) Lookup(Dictionary<string, (KindId Id, Type ClrType)> kinds, string name, string context)
        {
            if (!kinds.TryGetValue(name, out var kind))
                throw new InvalidOperationException($"\"{context}\" references undeclared kind \"{name}\" (not present in \"kinds\").");
            return kind;
        }

        private static Multiplicity ParseMultiplicity(string value, string relationName)
        {
            if (Enum.TryParse<Multiplicity>(value, ignoreCase: true, out var m)) return m;
            throw new InvalidOperationException(
                $"Relation \"{relationName}\": unknown multiplicity \"{value}\" (expected One/ZeroOrOne/OneOrMany/ZeroOrMany).");
        }

        private static PresenceCheck ParsePresenceCheck(string? value, string relationName)
        {
            if (string.IsNullOrEmpty(value)) return PresenceCheck.Nullable;
            if (Enum.TryParse<PresenceCheck>(value, ignoreCase: true, out var p)) return p;
            throw new InvalidOperationException(
                $"Relation \"{relationName}\": unknown presenceCheck \"{value}\" (expected Nullable/NilIdentifier).");
        }

        // Scans currently loaded assemblies for a type by its full
        // (namespace-qualified) name. Throws rather than guessing if the
        // name is found in more than one loaded assembly - see the
        // conversation this was designed in: namespace alone already
        // disambiguates same-named classes across different modules, so a
        // multi-assembly hit here means the exact same namespace+class
        // exists twice (e.g. two copies/versions of one generated
        // assembly loaded at once), which is always worth failing loudly
        // on rather than silently picking one.
        private static Type ResolveType(string fullName)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => SafeGetType(a, fullName))
                .Where(t => t != null)
                .Cast<Type>()
                .ToList();

            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"\"{fullName}\" exists in {matches.Count} loaded assemblies at once " +
                    $"({string.Join(", ", matches.Select(t => t.Assembly.GetName().Name))}) - " +
                    "check for a duplicate/old copy of the same generated assembly being loaded twice.");
            if (matches.Count == 0)
                throw new InvalidOperationException($"\"{fullName}\" was not found in any loaded assembly.");

            return matches[0];
        }

        private static Type? SafeGetType(System.Reflection.Assembly asm, string fullName)
        {
            try { return asm.GetType(fullName); }
            catch (Exception) { return null; } // dynamic/reflection-only assemblies can throw here
        }

        private sealed class ModuleSchema
        {
            public string Module { get; set; } = "";
            public List<string> RootKinds { get; set; } = new();
            public List<KindSchema> Kinds { get; set; } = new();
            public List<RelationSchema> Relations { get; set; } = new();
        }

        private sealed class KindSchema
        {
            public string Name { get; set; } = "";
            public string ClrType { get; set; } = "";
            public string KeyField { get; set; } = "";
        }

        private sealed class RelationSchema
        {
            public string Name { get; set; } = "";
            public string Direction { get; set; } = "";

            // unidirectional
            public string? From { get; set; }
            public string? To { get; set; }
            public string? FromField { get; set; }
            public string? Multiplicity { get; set; }

            // bidirectional
            public string? Left { get; set; }
            public string? Right { get; set; }
            public string? LeftField { get; set; }
            public string? RightField { get; set; }
            public string? LeftToRightMultiplicity { get; set; }
            public string? RightToLeftMultiplicity { get; set; }

            public string? PresenceCheck { get; set; }
        }
    }
}
