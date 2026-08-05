using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
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

    // How a reflection-built accessor decides a ZeroOrOne/optional reference
    // is absent. Nullable checks Nullable<T>.HasValue; NilIdentifier treats a
    // composite identifier whose sub-fields are all zero as absent (matches
    // NGVA's convention of filling unset references with A_resourceId=0,
    // A_instanceId=0 rather than leaving the field null).
    public enum PresenceCheck
    {
        Nullable,
        NilIdentifier,
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

        // Serializes index writers per relation; readers stay lock-free.
        public object WriteGate { get; } = new object();

        // If set, link validity requires reciprocal match.
        public RelationDef? Reciprocal { get; set; }

        // True for the R->L half of a bidirectional pair. Used only when
        // AggregationEngine.IsolateAggregateBoundaries is enabled.
        public bool IsReciprocalSecondary { get; set; }

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
                var arr = new T[list.Count];
                for (int i = 0; i < list.Count; i++) arr[i] = (T)list[i];
                items = arr;
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
        private readonly ConcurrentDictionary<KindId, ImmutableList<RelationDef>> _relsByFrom = new();
        private readonly ConcurrentDictionary<KindId, ImmutableList<RelationDef>> _relsByTo = new();
        private readonly ConcurrentDictionary<KindId, bool> _rootKinds = new();

        // Per-root emission state: computation gate + FIFO queue so that
        // subscriber callbacks run outside the gate (see EmitIfComplete).
        private sealed class RootEmitState
        {
            public readonly object Gate = new object();
            public readonly Queue<PendingEmit> Pending = new();
            public bool Draining;
        }

        private readonly struct PendingEmit
        {
            public readonly AggregateSnapshot Snapshot;
            public readonly Action<RootId, AggregateSnapshot>? Global;
            public readonly Action<RootId, AggregateSnapshot>[] Handlers;

            public PendingEmit(AggregateSnapshot snapshot, Action<RootId, AggregateSnapshot>? global, Action<RootId, AggregateSnapshot>[] handlers)
            {
                Snapshot = snapshot;
                Global = global;
                Handlers = handlers;
            }
        }

        private readonly ConcurrentDictionary<RootId, RootEmitState> _emitByRoot = new();

        // Last emitted member set per root; populated only while
        // SuppressUnchangedSnapshots is enabled.
        private readonly ConcurrentDictionary<RootId, Dictionary<EntityId, object>> _lastEmitted = new();

        // Root-kind routing handlers
        private readonly ConcurrentDictionary<KindId, List<Action<RootId, AggregateSnapshot>>> _rootKindHandlers = new();

        // Global event
        public event Action<RootId, AggregateSnapshot>? OnAggregate;

        // -------------------------
        // Opt-in behavior switches. All default to false so that the engine
        // behaves exactly as before; enable per deployment as needed.
        // -------------------------

        // Emit a root only when the updated entity is part of that root's
        // snapshot (or is the root itself). Prevents re-notifying sibling
        // aggregates that are merely graph-reachable from the update.
        public bool EmitOnlyAffectedRoots { get; set; }

        // Keep aggregate boundaries: Assemble/IsComplete do not traverse the
        // reverse half of bidirectional relations and do not descend into
        // other root-kind instances. Reciprocal validation still applies.
        public bool IsolateAggregateBoundaries { get; set; }

        // Skip emission when the snapshot has the same members with the same
        // object instances as the previous emission for that root.
        public bool SuppressUnchangedSnapshots { get; set; }

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

        // Reflection-based counterpart to RegisterKind<T>, for JSON/schema-
        // driven registration (a generator or config loader supplies the
        // CLR type and key field/property name at startup instead of a
        // hand-written lambda). Internally builds the exact same shape of
        // Func<object,long> the generic overload builds, so KindDef and
        // everything downstream is unaffected - this is purely an
        // alternate way to construct that delegate. The reflection/
        // Expression-tree cost is paid once here, at registration time,
        // not per Upsert.
        public KindId RegisterKind(Type clrType, string keyFieldName, Func<long, long, long>? combineIdentifier = null)
        {
            if (clrType == null) throw new ArgumentNullException(nameof(clrType));
            if (string.IsNullOrEmpty(keyFieldName)) throw new ArgumentNullException(nameof(keyFieldName));

            var combine = combineIdentifier ?? DefaultCombineIdentifier;
            var member = ResolveMember(clrType, keyFieldName)
                ?? throw new InvalidOperationException($"Key field/property '{keyFieldName}' not found on {clrType.FullName}");
            var getter = CompileGetter(clrType, member);
            var toLong = BuildElementConverter(MemberReturnType(member), combine);

            var kind = _typeToKind.GetOrAdd(clrType, _ => new KindId(Interlocked.Increment(ref _nextKind)));
            _kinds.TryAdd(kind, new KindDef(kind, clrType, obj => toLong(getter(obj))));
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

        // Reflection-based counterpart to RegisterUnidirectional<TFrom,TTo>.
        // fromFieldName names the field/property on fromClrType carrying the
        // foreign key(s): a scalar or composite-identifier member for
        // One/ZeroOrOne, or an enumerable of either for OneOrMany/ZeroOrMany.
        // presenceCheck only matters for ZeroOrOne.
        public void RegisterUnidirectional(
            string name,
            KindId fromKind,
            KindId toKind,
            Multiplicity multiplicity,
            Type fromClrType,
            string fromFieldName,
            PresenceCheck presenceCheck = PresenceCheck.Nullable,
            Func<long, long, long>? combineIdentifier = null)
        {
            EnsureKindRegistered(fromKind, nameof(fromKind));
            EnsureKindRegistered(toKind, nameof(toKind));

            var accessor = BuildRelationAccessor(
                fromClrType, fromFieldName, multiplicity, presenceCheck,
                combineIdentifier ?? DefaultCombineIdentifier);

            AddRelation(new RelationDef(name, fromKind, toKind, multiplicity, accessor));
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
            rl.IsReciprocalSecondary = true;

            AddRelation(lr);
            AddRelation(rl);
        }

        // Reflection-based counterpart to RegisterBidirectional<TLeft,TRight>.
        // Same field-naming contract as the non-generic RegisterUnidirectional
        // on each side; reciprocal validation is wired up exactly as in the
        // generic overload.
        public void RegisterBidirectional(
            string name,
            KindId leftKind,
            KindId rightKind,
            Multiplicity leftToRightMultiplicity,
            Multiplicity rightToLeftMultiplicity,
            Type leftClrType,
            string leftFieldName,
            Type rightClrType,
            string rightFieldName,
            PresenceCheck leftPresenceCheck = PresenceCheck.Nullable,
            PresenceCheck rightPresenceCheck = PresenceCheck.Nullable,
            Func<long, long, long>? combineIdentifier = null)
        {
            EnsureKindRegistered(leftKind, nameof(leftKind));
            EnsureKindRegistered(rightKind, nameof(rightKind));

            var combine = combineIdentifier ?? DefaultCombineIdentifier;
            var leftAccessor = BuildRelationAccessor(leftClrType, leftFieldName, leftToRightMultiplicity, leftPresenceCheck, combine);
            var rightAccessor = BuildRelationAccessor(rightClrType, rightFieldName, rightToLeftMultiplicity, rightPresenceCheck, combine);

            var lr = new RelationDef(name + ":L->R", leftKind, rightKind, leftToRightMultiplicity, leftAccessor);
            var rl = new RelationDef(name + ":R->L", rightKind, leftKind, rightToLeftMultiplicity, rightAccessor);

            lr.Reciprocal = rl;
            rl.Reciprocal = lr;
            rl.IsReciprocalSecondary = true;

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
        // Typed aggregate binding: lets a subscriber declare the shape of
        // the snapshot it wants (a POCO) instead of calling TryGetMany/
        // TryGetOne per kind. Member types are matched against registered
        // Kinds via reflection, cached per POCO type.
        // -------------------------

        private enum MemberShape { None, Single, List, Array }

        private sealed class AggregateMember
        {
            public Type ElementType = null!;
            public MemberShape Shape;
            public Action<object, object?> Set = null!;
        }

        private sealed class AggregateBinder
        {
            public AggregateMember[] Members = Array.Empty<AggregateMember>();
        }

        private static readonly ConcurrentDictionary<Type, AggregateBinder> _binders = new();

        private static MemberShape DescribeMember(Type memberType, out Type elementType)
        {
            if (memberType.IsArray)
            {
                elementType = memberType.GetElementType()!;
                return elementType.IsClass ? MemberShape.Array : MemberShape.None;
            }

            if (memberType.IsGenericType)
            {
                var def = memberType.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>)
                    || def == typeof(ICollection<>) || def == typeof(IEnumerable<>))
                {
                    elementType = memberType.GetGenericArguments()[0];
                    return elementType.IsClass ? MemberShape.List : MemberShape.None;
                }
            }

            if (memberType.IsClass && memberType != typeof(string))
            {
                elementType = memberType;
                return MemberShape.Single;
            }

            elementType = memberType;
            return MemberShape.None;
        }

        private static AggregateBinder GetBinder(Type aggregateType) => _binders.GetOrAdd(aggregateType, t =>
        {
            var members = new List<AggregateMember>();

            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                var shape = DescribeMember(prop.PropertyType, out var elementType);
                if (shape == MemberShape.None) continue;
                var p = prop;
                members.Add(new AggregateMember { ElementType = elementType, Shape = shape, Set = (obj, val) => p.SetValue(obj, val) });
            }

            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsInitOnly) continue;
                var shape = DescribeMember(field.FieldType, out var elementType);
                if (shape == MemberShape.None) continue;
                var f = field;
                members.Add(new AggregateMember { ElementType = elementType, Shape = shape, Set = (obj, val) => f.SetValue(obj, val) });
            }

            return new AggregateBinder { Members = members.ToArray() };
        });

        private TAggregate BindAggregate<TAggregate>(AggregateSnapshot snapshot) where TAggregate : class, new()
        {
            var binder = GetBinder(typeof(TAggregate));
            var instance = new TAggregate();

            foreach (var member in binder.Members)
            {
                // Members whose type was never registered via RegisterKind
                // are left at their default value (e.g. plain metadata
                // fields the caller added to the POCO).
                if (!_typeToKind.TryGetValue(member.ElementType, out var kind)) continue;
                snapshot.Raw.TryGetValue(kind, out var list);

                switch (member.Shape)
                {
                    case MemberShape.Single:
                        member.Set(instance, list is { Count: > 0 } ? list[0] : null);
                        break;
                    case MemberShape.Array:
                    case MemberShape.List:
                    {
                        var count = list?.Count ?? 0;
                        var array = Array.CreateInstance(member.ElementType, count);
                        for (int i = 0; i < count; i++) array.SetValue(list![i], i);
                        member.Set(instance, member.Shape == MemberShape.Array
                            ? array
                            : Activator.CreateInstance(typeof(List<>).MakeGenericType(member.ElementType), (object)array));
                        break;
                    }
                }
            }

            return instance;
        }

        // Same as SubscribeRootKind(KindId, Action<RootId, AggregateSnapshot>),
        // but delivers a caller-defined POCO instead of a raw snapshot. Each
        // public settable field/property of TAggregate whose type (or element
        // type, for arrays/List<T>/IReadOnlyList<T>/...) matches a registered
        // Kind is populated automatically; unmatched members are left as-is.
        public IDisposable SubscribeRootKind<TAggregate>(KindId rootKind, Action<RootId, TAggregate> handler)
            where TAggregate : class, new()
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return SubscribeRootKind(rootKind, (root, snapshot) => handler(root, BindAggregate<TAggregate>(snapshot)));
        }

        // -------------------------
        // Upsert / Remove
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
            var changed = new EntityId(kind, key);
            foreach (var root in FindImpactedRoots(changed))
                EmitIfComplete(root, changed);
        }

        // Removes an entity and its outgoing relation edges. Incoming edges
        // owned by other entities remain until those entities are updated,
        // mirroring the attribute-driven nature of the indexes. No emission
        // is triggered by removal.
        public bool Remove(KindId kind, long key)
        {
            if (!_kinds.TryGetValue(kind, out var kd)) return false;
            if (!kd.Store.TryRemove(key, out _)) return false;

            if (_relsByFrom.TryGetValue(kind, out var relsFrom))
            {
                foreach (var rel in relsFrom)
                    ClearIndexesForFrom(rel, key);
            }

            if (_rootKinds.ContainsKey(kind))
            {
                var rid = new RootId(kind, key);
                _emitByRoot.TryRemove(rid, out _);
                _lastEmitted.TryRemove(rid, out _);
            }
            return true;
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

        // -------------------------
        // Reflection-based registration internals (used by the non-generic
        // RegisterKind/RegisterUnidirectional/RegisterBidirectional
        // overloads). All reflection and Expression-tree compilation here
        // runs once per Register* call, at startup - never per Upsert.
        // -------------------------

        // Default composite-identifier combiner, matching the sub-field
        // names NGVA's own data model uses (A_sourceID.A_resourceId /
        // A_sourceID.A_instanceId). Callers with a different combination
        // rule pass their own via the combineIdentifier parameter.
        public static long DefaultCombineIdentifier(long resourceId, long instanceId) =>
            (resourceId << 32) | (instanceId & 0xFFFFFFFFL);

        private static readonly Type[] IntegerLikeTypes =
        {
            typeof(long), typeof(ulong), typeof(int), typeof(uint),
            typeof(short), typeof(ushort), typeof(byte), typeof(sbyte),
        };

        private static bool IsIntegerLike(Type t) => Array.IndexOf(IntegerLikeTypes, t) >= 0;

        private static MemberInfo ResolveMember(Type ownerType, string name)
        {
            var prop = ownerType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) return prop;
            var field = ownerType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field;
            throw new InvalidOperationException($"Field/property '{name}' not found on {ownerType.FullName}");
        }

        private static Type MemberReturnType(MemberInfo member) => member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _ => throw new InvalidOperationException("member must be a field or property"),
        };

        // Compiles a cached Func<object,object?> for one field/property
        // access, boxing value types. Reflection cost (GetProperty/GetField)
        // and Expression compilation both happen once, here.
        private static Func<object, object?> CompileGetter(Type ownerType, MemberInfo member)
        {
            var param = Expression.Parameter(typeof(object), "obj");
            var castParam = Expression.Convert(param, ownerType);
            Expression access = member switch
            {
                PropertyInfo p => Expression.Property(castParam, p),
                FieldInfo f => Expression.Field(castParam, f),
                _ => throw new InvalidOperationException("member must be a field or property"),
            };
            var boxed = Expression.Convert(access, typeof(object));
            return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
        }

        private static Type? GetEnumerableElementType(Type t)
        {
            if (t.IsArray) return t.GetElementType();
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>)
                    || def == typeof(ICollection<>) || def == typeof(IEnumerable<>))
                    return t.GetGenericArguments()[0];
            }
            foreach (var iface in t.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return iface.GetGenericArguments()[0];
            }
            return null;
        }

        // Builds a converter from one element's raw value (an integer-like
        // boxed value, or a composite identifier instance) to a long key.
        private static Func<object?, long> BuildElementConverter(Type elementType, Func<long, long, long> combine)
        {
            var underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;

            if (IsIntegerLike(underlying))
                return raw => raw == null ? 0L : Convert.ToInt64(raw);

            var resMember = ResolveMember(underlying, "A_resourceId");
            var instMember = ResolveMember(underlying, "A_instanceId");
            var resGetter = CompileGetter(underlying, resMember);
            var instGetter = CompileGetter(underlying, instMember);

            return raw =>
            {
                if (raw == null) return 0L;
                long r = Convert.ToInt64(resGetter(raw));
                long i = Convert.ToInt64(instGetter(raw));
                return combine(r, i);
            };
        }

        // True when a composite identifier's sub-fields are both zero (the
        // NGVA convention for "no reference"), or the raw value is null.
        // Returns false for identifier types with no A_resourceId/
        // A_instanceId pair - those can never be "NIL" under this check.
        private static bool IsNilIdentifierValue(object? raw, Type memberType)
        {
            if (raw == null) return true;
            var resProp = memberType.GetProperty("A_resourceId", BindingFlags.Public | BindingFlags.Instance);
            var resField = resProp == null ? memberType.GetField("A_resourceId", BindingFlags.Public | BindingFlags.Instance) : null;
            var instProp = memberType.GetProperty("A_instanceId", BindingFlags.Public | BindingFlags.Instance);
            var instField = instProp == null ? memberType.GetField("A_instanceId", BindingFlags.Public | BindingFlags.Instance) : null;
            if (resProp == null && resField == null) return false;
            if (instProp == null && instField == null) return false;

            object? resVal = resProp != null ? resProp.GetValue(raw) : resField!.GetValue(raw);
            object? instVal = instProp != null ? instProp.GetValue(raw) : instField!.GetValue(raw);
            return Convert.ToInt64(resVal) == 0 && Convert.ToInt64(instVal) == 0;
        }

        // Builds the Func<object,IEnumerable<long>> a RelationDef needs,
        // covering all four multiplicities and both presence conventions,
        // from a single (owner type, field name) pair.
        private static Func<object, IEnumerable<long>> BuildRelationAccessor(
            Type ownerType, string fieldName, Multiplicity multiplicity,
            PresenceCheck presenceCheck, Func<long, long, long> combine)
        {
            var member = ResolveMember(ownerType, fieldName);
            var memberType = MemberReturnType(member);
            var getter = CompileGetter(ownerType, member);

            bool isMany = multiplicity is Multiplicity.OneOrMany or Multiplicity.ZeroOrMany;

            if (isMany)
            {
                var elementType = GetEnumerableElementType(memberType)
                    ?? throw new InvalidOperationException(
                        $"'{ownerType.FullName}.{fieldName}' is not enumerable but multiplicity is {multiplicity}");
                var elementToLong = BuildElementConverter(elementType, combine);

                return obj =>
                {
                    var raw = getter(obj);
                    if (raw is not System.Collections.IEnumerable seq) return Array.Empty<long>();
                    var list = new List<long>();
                    foreach (var item in seq) list.Add(elementToLong(item));
                    return list;
                };
            }

            var scalarToLong = BuildElementConverter(memberType, combine);

            if (multiplicity == Multiplicity.One)
                return obj => AggregationEngine.One(scalarToLong(getter(obj)));

            // ZeroOrOne
            if (presenceCheck == PresenceCheck.NilIdentifier)
            {
                return obj =>
                {
                    var raw = getter(obj);
                    bool present = !IsNilIdentifierValue(raw, memberType);
                    return AggregationEngine.ZeroOrOne(present, present ? scalarToLong(raw) : 0L);
                };
            }

            // Nullable convention (default). Boxing a Nullable<T> yields a
            // real null when HasValue is false and a boxed T when true, so
            // getter(obj) already collapses to a plain null/non-null check -
            // no further reflection needed per call.
            return obj =>
            {
                var raw = getter(obj);
                bool present = raw != null;
                return AggregationEngine.ZeroOrOne(present, present ? scalarToLong(raw) : 0L);
            };
        }

        private void AddRelation(RelationDef rel)
        {
            _relsByFrom.AddOrUpdate(rel.From,
                _ => ImmutableList.Create(rel),
                (_, list) => list.Add(rel));

            _relsByTo.AddOrUpdate(rel.To,
                _ => ImmutableList.Create(rel),
                (_, list) => list.Add(rel));
        }

        private void UpdateIndexesForFrom(RelationDef rel, object fromEntity, long fromKey)
        {
            var newTargets = (rel.GetToKeysFromFrom(fromEntity) ?? Array.Empty<long>()).ToImmutableHashSet();

            lock (rel.WriteGate)
            {
                rel.Forward.TryGetValue(fromKey, out var oldTargets);
                oldTargets ??= ImmutableHashSet<long>.Empty;

                // Periodic republication with unchanged references: no-op.
                if (newTargets.SetEquals(oldTargets)) return;

                // Add new reverse links before switching Forward, and remove
                // stale ones after, so concurrent traversals see a superset
                // of edges (transient over-reach instead of missed roots).
                foreach (var added in newTargets.Except(oldTargets))
                {
                    rel.Reverse.AddOrUpdate(
                        added,
                        _ => ImmutableHashSet<long>.Empty.Add(fromKey),
                        (_, set) => set.Add(fromKey));
                }

                rel.Forward[fromKey] = newTargets;

                foreach (var removed in oldTargets.Except(newTargets))
                {
                    rel.Reverse.AddOrUpdate(
                        removed,
                        _ => ImmutableHashSet<long>.Empty,
                        (_, set) => set.Remove(fromKey));
                }
            }
        }

        private void ClearIndexesForFrom(RelationDef rel, long fromKey)
        {
            lock (rel.WriteGate)
            {
                if (!rel.Forward.TryRemove(fromKey, out var oldTargets) || oldTargets == null) return;

                foreach (var removed in oldTargets)
                {
                    rel.Reverse.AddOrUpdate(
                        removed,
                        _ => ImmutableHashSet<long>.Empty,
                        (_, set) => set.Remove(fromKey));
                }
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

        private void EmitIfComplete(RootId root, EntityId changed)
        {
            var state = _emitByRoot.GetOrAdd(root, _ => new RootEmitState());
            bool drain = false;

            lock (state.Gate)
            {
                if (!_kinds.TryGetValue(root.Kind, out var rootKindDef)) return;
                if (!rootKindDef.Store.ContainsKey(root.Key)) return;

                var snapshot = Assemble(root);
                if (!IsComplete(root, snapshot)) return;

                if (EmitOnlyAffectedRoots
                    && !(root.Kind == changed.Kind && root.Key == changed.Key)
                    && !SnapshotContains(snapshot, changed))
                    return;

                if (SuppressUnchangedSnapshots && !UpdateLastEmitted(root, snapshot))
                    return;

                Action<RootId, AggregateSnapshot>[] copy = Array.Empty<Action<RootId, AggregateSnapshot>>();
                if (_rootKindHandlers.TryGetValue(root.Kind, out var handlers))
                {
                    lock (handlers) copy = handlers.ToArray();
                }

                state.Pending.Enqueue(new PendingEmit(snapshot, OnAggregate, copy));

                if (!state.Draining)
                {
                    state.Draining = true;
                    drain = true;
                }
            }

            if (!drain) return;

            // Drain FIFO outside the gate so subscriber callbacks never run
            // while the gate is held. Re-entrant upserts from a callback are
            // enqueued above and picked up by this loop.
            while (true)
            {
                PendingEmit item;
                lock (state.Gate)
                {
                    if (state.Pending.Count == 0)
                    {
                        state.Draining = false;
                        return;
                    }
                    item = state.Pending.Dequeue();
                }

                try
                {
                    item.Global?.Invoke(root, item.Snapshot);
                    foreach (var h in item.Handlers) h(root, item.Snapshot);
                }
                catch
                {
                    // Release the drain flag so a later emit can resume the
                    // queue; the failing exception still reaches the caller.
                    lock (state.Gate) state.Draining = false;
                    throw;
                }
            }
        }

        private bool SnapshotContains(AggregateSnapshot snapshot, EntityId id)
        {
            if (!_kinds.TryGetValue(id.Kind, out var kd)) return false;
            if (!snapshot.Raw.TryGetValue(id.Kind, out var list)) return false;
            foreach (var obj in list)
            {
                if (kd.GetKey(obj) == id.Key) return true;
            }
            return false;
        }

        // Returns false when the snapshot is member-and-reference identical
        // to the previous emission for this root. Called under the root gate.
        private bool UpdateLastEmitted(RootId root, AggregateSnapshot snapshot)
        {
            var sig = new Dictionary<EntityId, object>();
            foreach (var (kind, list) in snapshot.Raw)
            {
                if (!_kinds.TryGetValue(kind, out var kd)) continue;
                foreach (var obj in list)
                    sig[new EntityId(kind, kd.GetKey(obj))] = obj;
            }

            if (_lastEmitted.TryGetValue(root, out var prev) && prev.Count == sig.Count)
            {
                var same = true;
                foreach (var kv in sig)
                {
                    if (!prev.TryGetValue(kv.Key, out var p) || !ReferenceEquals(p, kv.Value))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return false;
            }

            _lastEmitted[root] = sig;
            return true;
        }

        private bool SkipTraversal(RelationDef rel) =>
            IsolateAggregateBoundaries && rel.IsReciprocalSecondary;

        private bool IsForeignRoot(RootId root, EntityId cur) =>
            IsolateAggregateBoundaries
            && _rootKinds.ContainsKey(cur.Kind)
            && !(cur.Kind == root.Kind && cur.Key == root.Key);

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

                // Boundary isolation: reference other roots shallowly.
                if (IsForeignRoot(root, cur)) continue;

                if (_relsByFrom.TryGetValue(cur.Kind, out var outgoing))
                {
                    foreach (var rel in outgoing)
                    {
                        if (SkipTraversal(rel)) continue;
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

                // Boundary isolation: do not impose another root's obligations.
                if (IsForeignRoot(root, cur)) continue;

                if (!_relsByFrom.TryGetValue(cur.Kind, out var outgoing)) continue;

                foreach (var rel in outgoing)
                {
                    if (SkipTraversal(rel)) continue;

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
