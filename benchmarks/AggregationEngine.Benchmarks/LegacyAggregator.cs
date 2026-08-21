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
        private readonly Dictionary<long, LinearMount> _mounts = new Dictionary<long, LinearMount>();
        private readonly Dictionary<long, ActualMount> _actualMounts = new Dictionary<long, ActualMount>();
        private readonly Dictionary<long, LinearMountSpecification> _specifications = new Dictionary<long, LinearMountSpecification>();
        private readonly Dictionary<long, LinearSoftLimits> _softLimits = new Dictionary<long, LinearSoftLimits>();
        private readonly Dictionary<long, MountPart> _parts = new Dictionary<long, MountPart>();
        private readonly Dictionary<long, LinearTargetPosition> _targetPositions = new Dictionary<long, LinearTargetPosition>();

        // Hand-written reverse index: required because Specification is a
        // Shared Aggregation part and carries no back-reference of its own.
        private readonly Dictionary<long, HashSet<long>> _mountsBySpec = new Dictionary<long, HashSet<long>>();
        private readonly Dictionary<long, List<MountPart>> _partsByLinearMount = new Dictionary<long, List<MountPart>>();

        public int NotificationCount;
        public event Action<LinearMountAggregate>? OnLinearMountComplete;

        public void OnLinearMount(LinearMount m)
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

        public void OnSpecification(LinearMountSpecification s)
        {
            _specifications[s.SourceId] = s;

            if (_mountsBySpec.TryGetValue(s.SourceId, out var referencingMounts))
            {
                foreach (var mountId in referencingMounts)
                    TryComplete(mountId);
            }
        }

        public void OnSoftLimits(LinearSoftLimits s)
        {
            _softLimits[s.SourceId] = s;
            TryComplete(s.LinearMountSourceId);
        }

        public void OnMountPart(MountPart p)
        {
            _parts[p.SourceId] = p;
            if (!_partsByLinearMount.TryGetValue(p.LinearMountSourceId, out var list))
            {
                list = new List<MountPart>();
                _partsByLinearMount[p.LinearMountSourceId] = list;
            }
            list.Add(p);
            TryComplete(p.LinearMountSourceId);
        }

        public void OnTargetPosition(LinearTargetPosition t)
        {
            _targetPositions[t.SourceId] = t;
            TryComplete(t.LinearMountSourceId);
        }

        private void TryComplete(long mountId)
        {
            if (!_mounts.TryGetValue(mountId, out var mount)) return;
            if (!_actualMounts.TryGetValue(mount.ActualMountSourceId, out var actual)) return;
            if (!_specifications.TryGetValue(mount.SpecificationSourceId, out var spec)) return;
            LinearSoftLimits? softLimits = null;
            if (mount.SoftLimitsSourceId.HasValue &&
                !_softLimits.TryGetValue(mount.SoftLimitsSourceId.Value, out softLimits))
                return; // optional, but a declared reference must be present

            LinearTargetPosition? targetPosition = null;
            if (mount.TargetPositionSourceId.HasValue &&
                !_targetPositions.TryGetValue(mount.TargetPositionSourceId.Value, out targetPosition))
                return; // optional, but if referenced it must be present

            var parts = new List<MountPart>();
            foreach (var partId in mount.PartSourceIds)
            {
                if (!_parts.TryGetValue(partId, out var p)) return;
                parts.Add(p);
            }

            var result = new LinearMountAggregate
            {
                LinearMountSourceId = mountId,
                LinearMount = mount,
                ActualMount = actual,
                LinearMountSpecification = spec,
                LinearSoftLimits = softLimits,
                LinearTargetPosition = targetPosition,
                Parts = parts,
            };

            NotificationCount++;
            OnLinearMountComplete?.Invoke(result);
        }
    }
}
