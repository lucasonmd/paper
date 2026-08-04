# AggregationEngine — performance & structural analysis

Measured on: local dev machine, .NET 8, `dotnet run -c Release`,
`benchmarks/AggregationEngine.Benchmarks`. All numbers below are from an
actual run of the committed benchmark code (`Program.cs`); raw CSVs are in
`results/`. Two consecutive runs of Experiments 2–3 agreed within ~10%,
consistent with ordinary JIT/GC microbenchmark noise on a shared machine —
treat absolute numbers as indicative of scale, not a controlled lab result.

Scenario: the NGVA `C_Rotational_Mount` aggregate from the paper (Mount +
ActualMount base + Specification + SoftLimits + 0..* InhibitZone + 0..1
TargetPosition). `Specification` is modelled as **Shared Aggregation**
(AEP-4754 Vol V §5.5.1, item 3): several Mount instances of the same model
reference the *same* Specification instance, and — because a Specification
has no natural back-reference to every Mount that shares it — the relation
is registered **unidirectionally** (`Mount -> Specification`), not
bidirectionally.

## Experiment 1 — notification fan-out under Shared Aggregation

N mounts share one Specification. All N are brought to completion, then
Mount #0 is re-published **unchanged** 20 times. Table shows total
subscriber notifications fired during the 20 repeats.

| N (shared mounts) | Legacy | Engine (default) | Engine (+EmitOnlyAffectedRoots) | Engine (+affectedOnly+SuppressUnchanged) |
|---:|---:|---:|---:|---:|
| 1  | 20 | 20  | 20 | 0 |
| 2  | 20 | 40  | 20 | 0 |
| 5  | 20 | 100 | 20 | 0 |
| 10 | 20 | 200 | 20 | 0 |
| 20 | 20 | 400 | 20 | 0 |

**Reading.** This reproduces, exactly and mechanically, the "upsert A and B,
C fire too" observation: re-publishing one mount that shares a Specification
with N−1 siblings causes the default engine to re-notify all N (`20×N`),
because `FindImpactedRoots` must walk every referrer of a shared node to
know who *might* be affected. `EmitOnlyAffectedRoots` restores exact
legacy-equivalent precision (`20`, independent of N) by filtering at
emission time — the candidate-discovery walk still happens (see Experiment
2), but only genuinely affected roots are handed to subscribers. Adding
`SuppressUnchangedSnapshots` removes the redundant re-notifications of
Mount #0 itself, since its emitted member set is byte-identical across the
20 repeats: **0** subscriber calls for a genuinely no-op republish, matching
what a change-aware legacy implementation would also do (and which this
naive legacy baseline does *not* do — it still fires all 20).

## Experiment 2 — steady-state re-publish cost vs. aggregate size (µs / Upsert call)

| InhibitZones (V) | Legacy | Engine (default) | Engine (+IsolateAggregateBoundaries) | Engine (all 3 flags) |
|---:|---:|---:|---:|---:|
| 0   | 0.12  | 18.17  | 17.39  | 19.39  |
| 10  | 0.43  | 65.15  | 53.85  | 27.76  |
| 50  | 1.12  | 59.96  | 34.53  | 36.94  |
| 100 | 0.68  | 75.81  | 64.03  | 67.62  |
| 200 | 1.15  | 154.89 | 123.72 | 129.63 |
| 400 | 2.19  | 297.28 | 231.84 | 240.66 |
| 800 | 4.45  | 594.62 | 483.95 | 497.66 |

**Reading.** Both approaches scale roughly linearly in aggregate size, as
expected (O(V) dictionary work for Legacy; O(V) BFS + snapshot
materialization for the engine). The engine carries a **roughly two-order-
of-magnitude constant-factor overhead** over hand-written dictionary lookups
— the price of the general BFS traversal, `ImmutableHashSet` bookkeeping,
and per-emission `Dictionary`/`List` allocation that the legacy code simply
doesn't do. `IsolateAggregateBoundaries` measurably reduces this overhead
(skips the reciprocal half of bidirectional relations during
Assemble/IsComplete) even though this benchmark's topic model has no
boundary-correctness bug to fix — i.e. it is a real, disclosed performance
lever, not only a correctness one.

## Experiment 3 — completion latency (the Upsert call that completes the aggregate)

5 InhibitZones per mount, 5000 independent aggregates, fresh Stopwatch per
call.

| Config | p50 (µs) | p95 (µs) |
|---|---:|---:|
| Legacy | 0.10 | 0.50 |
| Engine (default) | 9.10–9.20 | 13.10–13.20 |
| Engine (all 3 flags) | 9.30–9.80 | 12.50–13.60 |

**Reading.** ~90× median latency overhead versus the hand-written baseline
at this aggregate size. The hardening flags do not change this figure
materially — they change *how many times* subscribers are called, not the
per-call assembly cost.

## Experiment 4 — order-independence (correctness, engine only)

All 4! = 24 arrival orderings of {Mount, ActualMount, Specification,
SoftLimits} were fed to a fresh engine instance and the subscriber was
checked for **exactly one** completion notification, occurring only after
the last part arrives, with a fully-populated snapshot.

**Result: 24/24 orderings pass.** (An earlier version of this benchmark, in
which `Mount<->ActualMount` was mistakenly registered as bidirectional
without a real reverse reference, produced 0/24 — the reciprocal-validation
check requires a genuine back-reference or the relation can never be
satisfied. Fixed by registering that relation unidirectionally, matching
the real NGVA IDL, where `C_Actual_Mount` carries no pointer back to the
`C_Rotational_Mount` that specializes it. Left here as a documented
pitfall: reciprocal validation is opt-in *per relation* and must not be
requested for relations whose reverse side is never populated.)

## Experiment 5 — code-size cost of adding a topic kind (static, not timed)

Concrete example: adding a 7th topic kind, `StreamingStatus`
(per-mount, optional, Composite Aggregation — same shape as
`TargetPosition`), to the 6-kind Mount aggregate already implemented in
`LegacyAggregator.cs` (132 lines total for 6 kinds) and `EngineHarness.cs`
(72 lines total for 6 kinds, including the shared Specification relation).

**Legacy** — touches 4 separate locations:

```csharp
// 1. new store field
private readonly Dictionary<long, StreamingStatus> _streamingStatuses = new();

// 2. new receive callback
public void OnStreamingStatus(StreamingStatus s)
{
    _streamingStatuses[s.SourceId] = s;
    TryComplete(s.MountSourceId);
}

// 3. TryComplete gains another optional-lookup block
StreamingStatus? streamingStatus = null;
if (mount.StreamingStatusSourceId.HasValue &&
    !_streamingStatuses.TryGetValue(mount.StreamingStatusSourceId.Value, out streamingStatus))
    return;

// 4. result construction gains a field
    StreamingStatus = streamingStatus,
```
→ **12 new/changed lines across 3 methods + the result type.**

**Engine** — touches 1 location (registration):

```csharp
var streamingKind = engine.RegisterKind<StreamingStatus>(s => s.SourceId);

engine.RegisterBidirectional<Mount, StreamingStatus>(
    "Mount<->StreamingStatus", mountKind, streamingKind,
    Multiplicity.ZeroOrOne, Multiplicity.One,
    m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
            m.StreamingStatusSourceId.HasValue, m.StreamingStatusSourceId.GetValueOrDefault()),
    s => global::TopicManager.Extensions.AggregationEngine.One(s.MountSourceId));
```
→ **7 new lines, 1 location, 0 existing methods modified.**

(Both approaches equally require adding `StreamingStatusSourceId` to the
`Mount` topic class itself — that cost is identical and excluded from the
comparison above.)

**Reading.** For one topic kind the raw line delta (12 vs 7) is modest; the
structural point is qualitative and compounds with N: every Legacy addition
touches the store, the callback, `TryComplete`, and the result type — four
places that must independently stay consistent — while every Engine
addition is a single declarative statement that cannot, by construction,
leave `TryComplete`-equivalent logic out of sync, because there is no
per-kind `TryComplete` to forget.

## Honest limitations of this benchmark

- Single machine, in-process, synthetic topic stream — no DDS transport,
  serialization, or network latency is included on either side; both
  approaches are measured from the same in-memory `Upsert`/`OnXxx` call.
- `LegacyAggregator` is a fair-effort baseline (it does maintain a
  hand-written reverse index for the shared Specification, matching what a
  competent implementation would do), not a strawman with the reverse index
  omitted.
- Experiment 2/3 absolute microsecond figures depend on machine/JIT state;
  the paper should report them as *this measurement*, not as a
  universal constant, and disclose the methodology above.
