using System;
using System.Collections.Generic;

namespace UsageExample.Dds
{
    // Minimal stand-in for the DDS subscription API, written for this
    // example so that it depends on no vendor SDK.
    //
    // A real integration replaces this with the vendor's DataReader:
    // typically you create a reader per topic and attach a listener whose
    // callback runs on the middleware's receive thread. Only two things
    // matter for wiring the aggregation engine in, and both are reproduced
    // here:
    //
    //   1. readers are created one per topic type, and
    //   2. each delivers received samples to a callback.
    //
    // A real reader also hands you a SampleInfo per sample. Production code
    // should check it before forwarding:
    //   - skip samples whose valid_data is false (they carry only an
    //     instance state change, no payload);
    //   - take() can return several samples at once, so loop over them;
    //   - when instance_state indicates the instance was disposed or lost
    //     its writers, call engine.Remove(kind, key) instead of Upsert, so
    //     the aggregate stops counting a topic that no longer exists.
    // Those branches are omitted here because this mock has no SampleInfo.

    /// <summary>Receives samples of one topic type.</summary>
    public interface IDataReader<T> where T : class
    {
        /// <summary>DDS topic name this reader is subscribed to.</summary>
        string TopicName { get; }

        /// <summary>Raised once per received sample.</summary>
        event Action<T> OnDataAvailable;
    }

    /// <summary>Creates readers. Stands in for a DDS DomainParticipant / Subscriber.</summary>
    public sealed class DdsParticipant
    {
        private readonly List<string> _createdTopics = new List<string>();

        /// <summary>Topics a reader has been created for, in creation order.</summary>
        public IReadOnlyList<string> CreatedTopics => _createdTopics;

        public IDataReader<T> CreateReader<T>(string topicName) where T : class
        {
            if (string.IsNullOrEmpty(topicName)) throw new ArgumentNullException(nameof(topicName));
            _createdTopics.Add(topicName);
            return new MockDataReader<T>(topicName);
        }

        private sealed class MockDataReader<T> : IDataReader<T> where T : class
        {
            public string TopicName { get; }
            public event Action<T>? OnDataAvailable;

            public MockDataReader(string topicName) => TopicName = topicName;

            // Present so the type compiles as a working reader; a real
            // reader raises OnDataAvailable from the middleware's receive
            // thread instead of being driven from application code.
            public void Deliver(T sample) => OnDataAvailable?.Invoke(sample);
        }
    }
}
