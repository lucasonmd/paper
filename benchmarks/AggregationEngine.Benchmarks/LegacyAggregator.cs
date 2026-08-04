using System;
using System.Collections.Generic;

namespace AggregationEngine.Benchmarks
{
    // Baseline mirroring the pre-engine implementation style described in
    // the paper's discussion: one receive callback per topic kind, each
    // storing into its own Dictionary<long, object>-shaped store keyed by
    // sourceId, then checking whether the owning Mount aggregate can now be
    // completed by looking the required sourceIds up in the other topics'
    // dictionaries.
    //
    // To make the comparison fair rather than a strawman:
    //  - OnSpecification maintains an explicit reverse index (specId ->
    //    referencing mount ids) so that a Specification update correctly
    //    re-completes every Mount that shares it (matching the engine's
    //    correct behavior for that direction).
    //  - OnMount only re-checks the Mount's own sourceId - it has no reason
    //    to touch unrelated mounts, which is exactly the asymmetry being
    //    measured against the engine's default (reachability-based) fan-out.
    public sealed class LegacyAggregator
    {
        private readonly Dictionary<long, Mount> _mounts = new Dictionary<long, Mount>();
        private readonly Dictionary<long, ActualMount> _actualMounts = new Dictionary<long, ActualMount>();
        private readonly Dictionary<long, Specification> _specifications = new Dictionary<long, Specification>();
        private readonly Dictionary<long, SoftLimits> _softLimits = new Dictionary<long, SoftLimits>();
        private readonly Dictionary<long, InhibitZone> _zones = new Dictionary<long, InhibitZone>();
        private readonly Dictionary<long, TargetPosition> _targetPositions = new Dictionary<long, TargetPosition>();

        // Hand-written reverse index: required because Specification is a
        // Shared Aggregation part and carries no back-reference of its own.
        private readonly Dictionary<long, HashSet<long>> _mountsBySpec = new Dictionary<long, HashSet<long>>();
        private readonly Dictionary<long, List<InhibitZone>> _zonesByMount = new Dictionary<long, List<InhibitZone>>();

        public int NotificationCount;
        public event Action<MountAggregate>? OnMountComplete;

        public void OnMount(Mount m)
        {
            _mounts[m.SourceId] = m;

            if (!_mountsBySpec.TryGetValue(m.SpecificationSourceId, out var set))
            {
                set = new HashSet<long>();
                _mountsBySpec[m.SpecificationSourceId] = set;
            }
            set.Add(m.SourceId);

            TryComplete(m.SourceId);
        }

        public void OnActualMount(ActualMount a)
        {
            _actualMounts[a.SourceId] = a;
            // ActualMount is per-instance (1:1); the owning mount id is not
            // carried on ActualMount itself in this model, so re-checking it
            // happens naturally when the owning Mount next arrives, exactly
            // as in the original hand-written callbacks that only complete
            // the mount they were triggered from. (Kept intentionally
            // asymmetric with Specification to mirror real legacy code.)
        }

        public void OnSpecification(Specification s)
        {
            _specifications[s.SourceId] = s;

            if (_mountsBySpec.TryGetValue(s.SourceId, out var referencingMounts))
            {
                foreach (var mountId in referencingMounts)
                    TryComplete(mountId);
            }
        }

        public void OnSoftLimits(SoftLimits s)
        {
            _softLimits[s.SourceId] = s;
            TryComplete(s.MountSourceId);
        }

        public void OnInhibitZone(InhibitZone z)
        {
            _zones[z.SourceId] = z;
            if (!_zonesByMount.TryGetValue(z.MountSourceId, out var list))
            {
                list = new List<InhibitZone>();
                _zonesByMount[z.MountSourceId] = list;
            }
            list.Add(z);
            TryComplete(z.MountSourceId);
        }

        public void OnTargetPosition(TargetPosition t)
        {
            _targetPositions[t.SourceId] = t;
            TryComplete(t.MountSourceId);
        }

        private void TryComplete(long mountId)
        {
            if (!_mounts.TryGetValue(mountId, out var mount)) return;
            if (!_actualMounts.TryGetValue(mount.ActualMountSourceId, out var actual)) return;
            if (!_specifications.TryGetValue(mount.SpecificationSourceId, out var spec)) return;
            if (!_softLimits.TryGetValue(mount.SoftLimitsSourceId, out var softLimits)) return;

            TargetPosition? targetPosition = null;
            if (mount.TargetPositionSourceId.HasValue &&
                !_targetPositions.TryGetValue(mount.TargetPositionSourceId.Value, out targetPosition))
                return; // optional, but if referenced it must be present

            var zones = new List<InhibitZone>();
            foreach (var zoneId in mount.InhibitZoneSourceIds)
            {
                if (!_zones.TryGetValue(zoneId, out var z)) return;
                zones.Add(z);
            }

            var result = new MountAggregate
            {
                MountSourceId = mountId,
                Mount = mount,
                ActualMount = actual,
                Specification = spec,
                SoftLimits = softLimits,
                TargetPosition = targetPosition,
                InhibitZones = zones,
            };

            NotificationCount++;
            OnMountComplete?.Invoke(result);
        }
    }
}
