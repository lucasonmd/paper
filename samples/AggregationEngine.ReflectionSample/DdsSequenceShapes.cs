using System;
using System.Collections;
using System.Collections.Generic;

namespace AggregationEngine.ReflectionSample
{
    // Regression coverage for the collection shapes a real DDS IDL-to-C#
    // toolchain emits for `sequence<T>`, which are NOT plain arrays or
    // List<T>. The OMG C# mapping (and RTI Connext's Omg.Types) exposes a
    // bounded sequence as its own interface, e.g.
    //
    //     [Bound(100)] ISequence<global::P_LDM_Common.T_IdentifierType>
    //
    // where ISequence<T> : IList<T>, ICollection<T>, IEnumerable<T>, IEnumerable.
    //
    // AggregationEngine resolves the element type by walking GetInterfaces()
    // for IEnumerable<T> rather than matching a fixed list of collection
    // types, so any such vendor interface should work - but "should" isn't
    // "does", hence this test. The [Bound] attribute is irrelevant to the
    // engine (it inspects types, not attributes); it is reproduced here only
    // so the shape matches what the real generated code looks like.

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class BoundAttribute : Attribute
    {
        public int Max { get; }
        public BoundAttribute(int max) => Max = max;
    }

    // Same inheritance chain as the vendor interface.
    public interface ISequence<T> : IList<T>, ICollection<T>, IEnumerable<T>, IEnumerable
    {
    }

    // Minimal concrete implementation - a real one would enforce the bound.
    public sealed class BoundedSequence<T> : ISequence<T>
    {
        private readonly List<T> _items = new List<T>();

        public BoundedSequence() { }
        public BoundedSequence(IEnumerable<T> items) { _items.AddRange(items); }

        public T this[int index] { get => _items[index]; set => _items[index] = value; }
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(T item) => _items.Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(T item) => _items.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(T item) => _items.IndexOf(item);
        public void Insert(int index, T item) => _items.Insert(index, item);
        public bool Remove(T item) => _items.Remove(item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    // A mount whose zero-or-many reference is held in the vendor sequence
    // type rather than an array, with composite identifiers as elements -
    // i.e. the exact combination in C_Rotational_Mount's
    // A_movementInhibitZones_sourceID.
    public sealed class SeqMount
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_specification_sourceID;

        [Bound(100)]
        public ISequence<T_IdentifierType> A_movementInhibitZones_sourceID =
            new BoundedSequence<T_IdentifierType>();
    }

    public sealed class SeqSpecification
    {
        public T_IdentifierType A_sourceID;
    }

    public sealed class SeqInhibitZone
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_mount_sourceID;
    }
}
