using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace TopicManager.Extensions
{
    public enum Multiplicity
    {
        One,        // 1
        ZeroOrOne,  // 0..1
        OneOrMany,  // 1..*
        ZeroOrMany  // 0..*
    }

    public readonly record struct KindId(int Value);
    public readonly record struct EntityId(KindId Kind, long Key);
    public readonly record struct RootId(KindId Kind, long Key);

    internal sealed class KindDef
    {
        public KindId Kind { get; }
        public Type ClrType { get; }
        public Func<object, long> GetKey { get; }
        public ConcurrentDictionary<long, object> Store { get; } = new();

        public KindDef(KindId kind, Type clrType, Func<object, long> getKey)
        {
            Kind = kind;
            ClrType = clrType;
            GetKey = getKey;
        }
    }

    internal sealed class RelationDef
    {
        public string Name { get; }
        public KindId From { get; }
        public KindId To { get; }
        public Multiplicity FromToMultiplicity { get; }
        public Func<object, IEnumerable<long>> GetToKeysFromFrom { get; } // FromKey -> ToKeys

        public ConcurrentDictionary<long, ImmutableHashSet<long>> Forward { get; } = new(); // ToKey -> FromKeys
        public ConcurrentDictionary<long, ImmutableHashSet<long>> Reverse { get; } = new();

        // If set, link validity requires reciprocal match.
        public RelationDef? Reciprocal { get; set; }

        public RelationDef(string name, KindId from, KindId to, Multiplicity mult, Func<object, IEnumerable<long>> getToKeysFromFrom)
        {
            Name = name;
            From = from;
            To = to;
            FromToMultiplicity = mult;
            GetToKeysFromFrom = getToKeysFromFrom;
        }
    }

    public sealed class AggregateSnapshot
    {
        private readonly IReadOnlyDictionary<KindId, IReadOnlyList<object>> _byKind;

        internal AggregateSnapshot(IReadOnlyDictionary<KindId, IReadOnlyList<object>> byKind)
        {
            _byKind = byKind;
        }

        public IReadOnlyDictionary<KindId, IReadOnlyList<object>> Raw => _byKind;

        public bool TryGetMany<T>(KindId kind, out IReadOnlyList<T> items)
        {
            if (_byKind.TryGetValue(kind, out var list))
            {
                items = list.Cast<T>().ToArray();
                return true;
            }
            items = Array.Empty<T>();
            return false;
        }

        public bool TryGetOne<T>(KindId kind, out T? item) where T : class
        {
            if (_byKind.TryGetValue(kind, out var list) && list.Count > 0)
            {
                item = (T)list[0];
                return true;
            }
            item = null;
            return false;
        }
    }

    public sealed class AggregationEngine
    {
        private readonly ConcurrentDictionary<Type, KindId> _typeToKind = new();
        private int _nextKind = 0;
        private readonly ConcurrentDictionary<KindId, KindDef> _kinds = new();
        private readonly ConcurrentDictionary<KindId, List<RelationDef>> _relsByFrom = new();
        private readonly ConcurrentDictionary<KindId, List<RelationDef>> _relsByTo = new();
        private readonly ConcurrentDictionary<KindId, bool> _rootKinds = new();
        private readonly ConcurrentDictionary<RootId, object> _gateByRoot = new();

        // Root-kind routing handlers
        private readonly ConcurrentDictionary<KindId, List<Action<RootId, AggregateSnapshot>>> _rootKindHandlers = new();

        // Global event
        public event Action<RootId, AggregateSnapshot>? OnAggregate;

        // -------------------------
        // Registration
        // -------------------------

        public KindId RegisterKind<T>(Func<T, long> getKey) where T : class
        {
            if (getKey == null) throw new ArgumentNullException(nameof(getKey));

            var t = typeof(T);
            var kind = _typeToKind.GetOrAdd(t, _ => new KindId(Interlocked.Increment(ref _nextKind)));
            _kinds.TryAdd(kind, new KindDef(kind, t, o => getKey((T)o)));
            return kind;
        }

        public void RegisterRootKind(KindId kind) => _rootKinds[kind] = true;

        public void RegisterUnidirectional<TFrom, TTo>(
            string name,
            KindId fromKind,
            KindId toKind,
            Multiplicity fromToMultiplicity,
            Func<TFrom, IEnumerable<long>> getToKeysFromFrom)
            where TFrom : class
            where TTo : class
        {
            EnsureKindRegistered(fromKind, nameof(fromKind));
            EnsureKindRegistered(toKind, nameof(toKind));
            if (getToKeysFromFrom == null) throw new ArgumentNullException(nameof(getToKeysFromFrom));

            var rel = new RelationDef(
                name,
                fromKind,
                toKind,
                fromToMultiplicity,
                o => getToKeysFromFrom((TFrom)o) ?? Array.Empty<long>());

            AddRelation(rel);
        }

        // Bidirectional with reciprocal validation enabled
        public void RegisterBidirectional<TLeft, TRight>(
            string name,
            KindId leftKind,
            KindId rightKind,
            Multiplicity leftToRightMultiplicity,
            Multiplicity rightToLeftMultiplicity,
            Func<TLeft, IEnumerable<long>> getRightKeysFromLeft,
            Func<TRight, IEnumerable<long>> getLeftKeysFromRight)
            where TLeft : class
            where TRight : class
        {
            EnsureKindRegistered(leftKind, nameof(leftKind));
            EnsureKindRegistered(rightKind, nameof(rightKind));
            if (getRightKeysFromLeft == null) throw new ArgumentNullException(nameof(getRightKeysFromLeft));
            if (getLeftKeysFromRight == null) throw new ArgumentNullException(nameof(getLeftKeysFromRight));

            var lr = new RelationDef(
                name + ":L->R",
                leftKind,
                rightKind,
                leftToRightMultiplicity,
                o => getRightKeysFromLeft((TLeft)o) ?? Array.Empty<long>());

            var rl = new RelationDef(
                name + ":R->L",
                rightKind,
                leftKind,
                rightToLeftMultiplicity,
                o => getLeftKeysFromRight((TRight)o) ?? Array.Empty<long>());

            lr.Reciprocal = rl;
            rl.Reciprocal = lr;

            AddRelation(lr);
            AddRelation(rl);
        }

        // Subscribe handlers per root kind
        public IDisposable SubscribeRootKind(KindId rootKind, Action<RootId, AggregateSnapshot> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _rootKindHandlers.AddOrUpdate(
                rootKind,
                _ => new List<Action<RootId, AggregateSnapshot>> { handler },
                (_, list) => { lock (list) list.Add(handler); return list; });

            return new Unsubscriber(() =>
            {
                if (_rootKindHandlers.TryGetValue(rootKind, out var list))
                {
                    lock (list) list.Remove(handler);
                }
            });
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly Action _dispose;
            private int _done;
            public Unsubscriber(Action dispose) => _dispose = dispose;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) _dispose();
            }
        }

        // -------------------------
        // Upsert
        // -------------------------

        public void Upsert<T>(KindId kind, T entity) where T : class
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (!_kinds.TryGetValue(kind, out var kd))
                throw new InvalidOperationException($"Kind not registered: {kind}");

            var key = kd.GetKey(entity);

            // Store overwrite
            kd.Store[key] = entity;

            // Update relation indexes for outgoing edges from this kind
            if (_relsByFrom.TryGetValue(kind, out var relsFrom))
            {
                foreach (var rel in relsFrom)
                    UpdateIndexesForFrom(rel, entity, key);
            }

            // Find impacted roots (reverse + forward traversal)
            foreach (var root in FindImpactedRoots(new EntityId(kind, key)))
                EmitIfComplete(root);
        }

        // -------------------------
        // Store access
        // -------------------------

        public bool TryGet<T>(KindId kind, long key, out T? entity) where T : class
        {
            entity = null;
            if (!_kinds.TryGetValue(kind, out var kd)) return false;
            if (!kd.Store.TryGetValue(key, out var obj)) return false;
            entity = obj as T;
            return entity != null;
        }

        public IReadOnlyList<T> GetAll<T>(KindId kind) where T : class
        {
            if (!_kinds.TryGetValue(kind, out var kd)) return Array.Empty<T>();
            return kd.Store.Values.OfType<T>().ToArray();
        }

        public IReadOnlyList<long> GetAllKeys(KindId kind)
        {
            if (!_kinds.TryGetValue(kind, out var kd)) return Array.Empty<long>();
            return kd.Store.Keys.ToArray();
        }

        // resourceId lookups are caller-defined via key->resourceId converter
        public IReadOnlyList<T> GetByResourceId<T>(KindId kind, int resourceId, Func<long, int> getResourceIdFromKey) where T : class
        {
            if (getResourceIdFromKey == null) throw new ArgumentNullException(nameof(getResourceIdFromKey));
            if (!_kinds.TryGetValue(kind, out var kd)) return Array.Empty<T>();

            var result = new List<T>();
            foreach (var kv in kd.Store)
            {
                if (getResourceIdFromKey(kv.Key) == resourceId && kv.Value is T t)
                    result.Add(t);
            }
            return result;
        }

        public bool TryGetByResourceId<T>(KindId kind, int resourceId, Func<long, int> getResourceIdFromKey, out T? entity) where T : class
        {
            if (getResourceIdFromKey == null) throw new ArgumentNullException(nameof(getResourceIdFromKey));
            entity = null;
            if (!_kinds.TryGetValue(kind, out var kd)) return false;

            foreach (var kv in kd.Store)
            {
                if (getResourceIdFromKey(kv.Key) == resourceId && kv.Value is T t)
                {
                    entity = t;
                    return true;
                }
            }
            return false;
        }

        // -------------------------
        // Internals
        // -------------------------

        private void EnsureKindRegistered(KindId kind, string argName)
        {
            if (!_kinds.ContainsKey(kind))
                throw new InvalidOperationException($"Unregistered kind used: {argName}={kind}");
        }

        private void AddRelation(RelationDef rel)
        {
            _relsByFrom.AddOrUpdate(rel.From,
                _ => new List<RelationDef> { rel },
                (_, list) => { lock (list) list.Add(rel); return list; });

            _relsByTo.AddOrUpdate(rel.To,
                _ => new List<RelationDef> { rel },
                (_, list) => { lock (list) list.Add(rel); return list; });
        }

        private void UpdateIndexesForFrom(RelationDef rel, object fromEntity, long fromKey)
        {
            var keys = rel.GetToKeysFromFrom(fromEntity) ?? Array.Empty<long>();
            var newTargets = keys.ToImmutableHashSet();

            rel.Forward.TryGetValue(fromKey, out var oldTargets);
            oldTargets ??= ImmutableHashSet<long>.Empty;

            rel.Forward[fromKey] = newTargets;

            foreach (var removed in oldTargets.Except(newTargets))
            {
                rel.Reverse.AddOrUpdate(
                    removed,
                    _ => ImmutableHashSet<long>.Empty,
                    (_, set) => set.Remove(fromKey));
            }

            foreach (var added in newTargets.Except(oldTargets))
            {
                rel.Reverse.AddOrUpdate(
                    added,
                    _ => ImmutableHashSet<long>.Empty.Add(fromKey),
                    (_, set) => set.Add(fromKey));
            }
        }

        // Reverse + Forward traversal to find all impacted roots.
        private IEnumerable<RootId> FindImpactedRoots(EntityId start)
        {
            var visited = new HashSet<EntityId>();
            var q = new Queue<EntityId>();
            q.Enqueue(start);

            var roots = new HashSet<RootId>();

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!visited.Add(cur)) continue;

                if (_rootKinds.ContainsKey(cur.Kind))
                    roots.Add(new RootId(cur.Kind, cur.Key));

                // incoming edges (To == cur.Kind): go "up" via Reverse
                if (_relsByTo.TryGetValue(cur.Kind, out var incoming))
                {
                    foreach (var rel in incoming)
                    {
                        if (rel.Reverse.TryGetValue(cur.Key, out var fromKeys))
                        {
                            fromKeys ??= ImmutableHashSet<long>.Empty;
                            foreach (var fromKey in fromKeys)
                                q.Enqueue(new EntityId(rel.From, fromKey));
                        }
                    }
                }

                // outgoing edges (From == cur.Kind): go "down" via Forward
                if (_relsByFrom.TryGetValue(cur.Kind, out var outgoing))
                {
                    foreach (var rel in outgoing)
                    {
                        if (rel.Forward.TryGetValue(cur.Key, out var toKeys))
                        {
                            toKeys ??= ImmutableHashSet<long>.Empty;
                            foreach (var toKey in toKeys)
                                q.Enqueue(new EntityId(rel.To, toKey));
                        }
                    }
                }
            }

            return roots;
        }

        private void EmitIfComplete(RootId root)
        {
            var gate = _gateByRoot.GetOrAdd(root, _ => new object());
            lock (gate)
            {
                if (!_kinds.TryGetValue(root.Kind, out var rootKindDef)) return;
                if (!rootKindDef.Store.ContainsKey(root.Key)) return;

                var snapshot = Assemble(root);
                if (!IsComplete(root, snapshot)) return;

                // Global event
                OnAggregate?.Invoke(root, snapshot);

                // Root-kind routing
                if (_rootKindHandlers.TryGetValue(root.Kind, out var handlers))
                {
                    Action<RootId, AggregateSnapshot>[] copy;
                    lock (handlers) copy = handlers.ToArray();
                    foreach (var h in copy) h(root, snapshot);
                }
            }
        }

        // Assemble from root following Forward edges only.
        private AggregateSnapshot Assemble(RootId root)
        {
            var byKind = new Dictionary<KindId, Dictionary<long, object>>();

            void TryAdd(EntityId id)
            {
                if (!_kinds.TryGetValue(id.Kind, out var kd)) return;
                if (!kd.Store.TryGetValue(id.Key, out var entity)) return;

                if (!byKind.TryGetValue(id.Kind, out var map))
                {
                    map = new Dictionary<long, object>();
                    byKind[id.Kind] = map;
                }
                map[id.Key] = entity;
            }

            var visited = new HashSet<EntityId>();
            var q = new Queue<EntityId>();
            q.Enqueue(new EntityId(root.Kind, root.Key));

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!visited.Add(cur)) continue;

                TryAdd(cur);

                if (_relsByFrom.TryGetValue(cur.Kind, out var outgoing))
                {
                    foreach (var rel in outgoing)
                    {
                        if (rel.Forward.TryGetValue(cur.Key, out var toKeys))
                        {
                            toKeys ??= ImmutableHashSet<long>.Empty;
                            foreach (var toKey in toKeys)
                                q.Enqueue(new EntityId(rel.To, toKey));
                        }
                    }
                }
            }

            var final = byKind.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<object>)kv.Value.Values.ToList());

            return new AggregateSnapshot(final);
        }

        private bool IsComplete(RootId root, AggregateSnapshot snapshot)
        {
            var present = new HashSet<EntityId>();
            foreach (var (kind, list) in snapshot.Raw)
            {
                if (!_kinds.TryGetValue(kind, out var kd)) continue;
                foreach (var obj in list)
                {
                    var key = kd.GetKey(obj);
                    present.Add(new EntityId(kind, key));
                }
            }

            if (!present.Contains(new EntityId(root.Kind, root.Key))) return false;

            static bool RequiresAtLeastOne(Multiplicity m) =>
                m == Multiplicity.One || m == Multiplicity.OneOrMany;

            bool IsValidLink(RelationDef rel, long fromKey, long toKey)
            {
                // Unidirectional: accept if present.
                if (rel.Reciprocal is null) return true;

                // Bidirectional: require reciprocal match (toKey must reference fromKey).
                if (!rel.Reciprocal.Forward.TryGetValue(toKey, out var backTargets)) return false;
                backTargets ??= ImmutableHashSet<long>.Empty;
                return backTargets.Contains(fromKey);
            }

            var visited = new HashSet<EntityId>();
            var q = new Queue<EntityId>();
            q.Enqueue(new EntityId(root.Kind, root.Key));

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!visited.Add(cur)) continue;

                if (!_relsByFrom.TryGetValue(cur.Kind, out var outgoing)) continue;

                foreach (var rel in outgoing)
                {
                    if (!rel.Forward.TryGetValue(cur.Key, out var toKeys))
                        toKeys = ImmutableHashSet<long>.Empty;
                    toKeys ??= ImmutableHashSet<long>.Empty;

                    int validExistingCount = 0;
                    foreach (var toKey in toKeys)
                    {
                        var toId = new EntityId(rel.To, toKey);
                        if (!present.Contains(toId)) continue;
                        if (!IsValidLink(rel, cur.Key, toKey)) continue;

                        validExistingCount++;
                        q.Enqueue(toId);
                    }

                    if (RequiresAtLeastOne(rel.FromToMultiplicity) && validExistingCount == 0)
                        return false;
                }
            }

            return true;
        }

        // Helpers for relation registration
        public static IEnumerable<long> One(long key)
        {
            yield return key;
        }

        public static IEnumerable<long> Many(IEnumerable<long> keys) => keys ?? Array.Empty<long>();

        public static IEnumerable<long> ZeroOrOne(bool has, long key)
        {
            if (has) yield return key;
        }
    }
}
