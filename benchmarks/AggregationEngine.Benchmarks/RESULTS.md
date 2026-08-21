# AggregationEngine — performance & structural analysis

Measured on: local dev machine, .NET 8, `dotnet run -c Release`,
`benchmarks/AggregationEngine.Benchmarks`. All numbers below are from an
actual run of the committed benchmark code (`Program.cs`); raw CSVs are in
`results/`.

## 2026-08-21 — engine optimization; every timing figure below is superseded

`EmitIfComplete` was walking the aggregate graph three times per Upsert
(`FindImpactedRoots`, `Assemble`, `IsComplete`). `Assemble` and `IsComplete`
covered the identical edge set, and `IsComplete` rebuilt a HashSet of every
member to answer a question the store lookup in `Assemble` had already
answered. They are now one pass (`TryAssembleComplete`). Separately the
relation index moved off tree-shaped immutable collections:
`_relsByFrom`/`_relsByTo` from `ImmutableList<RelationDef>` to arrays, and
`Forward`/`Reverse` from `ImmutableHashSet<long>` to sorted `long[]`.
Copy-on-write publication is unchanged, so readers stay lock-free.

**Result: engine V=800 dropped from ~905 us to ~117 us (7.4x).** A three-arm
interleaved sweep (committed HEAD / pre-optimization working tree /
optimized, same harness, same session) also settled the open question about
the removal of `SuppressUnchangedSnapshots` and the stricter `IsComplete`:
HEAD and the pre-optimization working tree were indistinguishable at every V,
so the 605 us -> 900 us drift between sessions was machine state, never code.

**Behavioral equivalence checked, with one deliberate exception.** Identical
across all three arms: completion decisions, notification counts, snapshot
membership (kinds, counts, exact key sets at V=0/10/100/800), the
CompletenessCheck suite, EXP1, EXP4, ScalabilityStudy's legacy-vs-engine
verification, and the stdout of Sample/ReflectionSample/JsonSample. An
8-thread concurrent-upsert and reference-churn stress passes on both.
*The exception is ordering*: snapshot member lists and multi-root
notification order were in `ImmutableHashSet` enumeration order and are now
ascending by key. Only visible with scattered 64-bit sourceIds (small
sequential ids hash into ascending order anyway). This matters for
`TryGetOne<T>` called on a kind reachable below a 0..\* relation, where it
returns one of several members -- an ill-defined call either way, but the
member it picks changes.

### The legacy baseline is the noisy side, not the engine

EXP2 measures legacy first for each V, so it inherits whatever cache and heap
state the previous V's engine measurement left behind. Measured standalone,
V=800 legacy came out at 9.29 us; inside the suite the same code gave 4.2-7.9
us across runs. Strict alternation inside one process (both sides meeting the
same state, 6 reps) gives:

| V | legacy | engine (optimized) | ratio |
|---:|---|---|---:|
| 0   | 0.09-0.16, median 0.11 | 4.6-6.8, median 5.2 | 47x |
| 800 | 3.37-16.96, median 3.75 | 111.5-120.6, median 117.3 | 31x |

The engine side is tight (8% spread); legacy swings 5x because its absolute
values are small enough to be dominated by cache state. **Quote the ratio as
a few tens, not a precise multiple.** Legacy does build the full aggregate on
every timed call -- verified: 3000/3000 completions, 800 parts present in the
result each time -- so this is a like-for-like comparison, not a baseline
that skips the work.

The paper reports 5.2 / 117.3 us (engine) against 0.11 / 3.8 us (legacy) at
V=0 / V=800, from the alternating protocol above.

Everything below this line predates the optimization. The structural results
(EXP1 fan-out, EXP4 permutations, EXP5 code sizes) still hold as written; the
EXP2/EXP3 microsecond figures do not.

---

## Reproducibility — read this before quoting any timing number

The suite was re-run repeatedly after the numbers below were first recorded.
Findings:

- **EXP1 and EXP4 are deterministic** and reproduced byte-for-byte every
  time. These are the results to lean on.
- **EXP2 and EXP3 p50 reproduce within ~10%** — *provided the machine is
  thermally settled*. Re-measured EXP2 engine(default) at V=800 across five
  cooled runs: 606 / 631 / 619 / 614 / 642 µs against the 605 µs recorded
  below. EXP3 engine(default) p50: 10.1 / 9.7 / 9.6 / 9.4 / 9.8 µs against
  9.1 µs below.
- **Back-to-back runs without a cooldown inflate everything ~2×.** Five
  consecutive runs with no pause produced EXP2 V=800 figures of 1203 / 1001 /
  607 / 896 / 1012 µs and EXP3 p50 of 22.0 / 21.6 / 10.0 / 23.3 / 23.5 µs.
  This is CPU thermal/boost behaviour on a laptop-class machine, not a
  property of the code — but it means *any* timing number here is meaningless
  without stating that runs were spaced out.
- **Correction (2026-08-18): the "~25 s is enough" figure above was too
  optimistic, and the first run after a build must be discarded.** Six runs
  spaced 40 s apart produced V=800 engine figures of 913 / 1036 / 943 / 935 /
  921 / 843 µs — nowhere near settled. Re-running the same code with a **120 s
  cooldown after the build and 120 s between runs** landed back in the
  recorded band. The post-build run is the worst offender: in the controlled
  A/B below it came out at 971 µs against 611–618 µs for the runs that
  followed it, i.e. ~1.6× inflated. Protocol that actually works: build,
  wait 120 s, run, **discard that run**, then take measurements 120 s apart.
- **Controlled A/B: the working-tree changes are performance-neutral.**
  `SuppressUnchangedSnapshots` and its snapshot-comparison machinery were
  removed from the engine on 2026-08-18. To separate that from machine state,
  the committed code was checked out into a separate git worktree, built, and
  measured under the protocol above alongside the working tree:

  | arm | V=800 engine (settled runs) | ratio |
  |---|---|---|
  | HEAD (committed) | 618.3 / 611.2 µs | 138x / 143x |
  | working tree | 605.3 / 582.6 / 573.2 µs | 137x / 125x / 131x |

  The two arms are indistinguishable. Median over all five settled runs:
  **engine 605 µs, legacy 4.40 µs, ratio 137x** (range 125–143x). Per-run
  data is in `results/exp2_settled_repeats.csv`.
- **Session-to-session drift is larger than the within-session spread
  (2026-08-18, later).** The same commit measured twice on the same machine,
  both settled, hours apart:

  | | V=800 engine | V=800 legacy | ratio |
  |---|---|---|---|
  | HEAD, afternoon | 618 / 611 µs | 4.48 / 4.28 µs | 138x / 143x |
  | HEAD, evening | 918 / 955 / 840 µs | 7.92 / 5.50 / 7.24 µs | 116x / 174x / 116x |
  | working tree, evening | 876–953 µs (median 934) | 4.97–7.34 µs | 119–188x |

  HEAD moved 1.5x between sessions with no code change at all, and the
  evening working tree sits within 2% of evening HEAD. So the engine changes
  of that day (the stricter `IsComplete`, SoftLimits 0..1) are
  **performance-neutral**, and the absolute microsecond figures track machine
  state more than they track the code. Cooldown protocol does not rescue
  this: these were 300 s gaps and the values were stable *within* each
  session.
- **Consequence for the ratio.** Pooling both settled sessions gives roughly
  **115–190x** at V=800, not the 125–143x that one session suggested. Quote
  it as a two-order-of-magnitude constant factor; a figure like "140x" reads
  as more precision than this measurement supports. The V=0 absolute figure
  is the durable one: 17.7–18.3 µs across 13 measurements spanning both
  sessions and both code versions.
- **Quote the ratio as ~140x, not a tighter figure.** Five settled runs give
  125–143x at V=800. Anything more precise than "roughly 140x, two orders of
  magnitude" is reading signal into run-to-run variance.
- **EXP3 p95 does not reproduce and should not be quoted.** Observed values
  for engine(default) p95 span 11.4–23.7 µs across runs whose p50 was stable
  at ~10 µs. The tail is dominated by GC pauses and OS scheduling, i.e. it
  measures the machine, not the engine.
- **The EXP3 *ratio* against legacy is not a sound figure either.** EXP5
  (below) shows the legacy p50 of 0.100 µs is exactly one `Stopwatch` tick —
  the baseline sits on the timer's resolution floor, so the true legacy
  latency is somewhere in (0, 0.1] µs and unresolvable. Any "engine is N×
  slower" derived from EXP3 is therefore a *lower bound*, not a measurement.
  The EXP2 ratio does not have this problem: at V=800 both sides (4.5 µs =
  44 ticks, 605 µs = 6050 ticks) are far above the floor.

Scenario: the NGVA `C_Linear_Mount` aggregate from the paper, taken from
**AEP-4754 Vol V, Fig. 6** (clause 3.6.3, *NGVA Class Model Example: Mount
Data Model Domain Fragment*):

| Association (Fig. 6) | Far end | Near end |
|---|---|---|
| `Linear_Mount` --\|> `Actual_Mount` | generalization | — |
| `Linear_Mount` — `Linear_Mount_Specification` | `specification` 1 | `specifiedLinearMounts` 0..* |
| `Linear_Mount` — `Linear_Soft_Limits` | `softLimits` 0..1 | `linearMount` 1 |
| `Linear_Mount` — `Linear_Target_Position` | `targetPosition` 0..1 | `linearMount` 1 |

`Specification` is **Shared Aggregation** (AEP-4754 Vol V §5.5.1, item 3),
which Fig. 6 states outright: the far end of that association is
`specifiedLinearMounts` **0..\***, so several `Linear_Mount` instances of
the same model reference the *same* Specification instance. Because a
Specification carries no back-reference to every mount that shares it, the
relation is registered **unidirectionally** (`LinearMount -> Specification`),
not bidirectionally.

**One deliberate deviation from Fig. 6** is disclosed here and in the
paper's Section 6: `MountPart` is a **synthetic** 0..* part topic. It is *not* in Fig. 6 —
   the linear fragment has no zero-or-many association — and exists only so
   EXP2 can vary aggregate size V. It is shaped exactly like a Composite
   Aggregation part (own sourceId + back-reference).

## Experiment 1 — notification fan-out under Shared Aggregation

N mounts share one Specification (Fig. 6's `specifiedLinearMounts 0..*`). All N
are brought to completion, then the shared Specification is republished 20
times. The table counts completed-aggregate notifications.

| N (shared mounts) | republishes | notifications | per republish |
|---:|---:|---:|---:|
| 1  | 20 | 20  | 1  |
| 2  | 20 | 40  | 2  |
| 5  | 20 | 100 | 5  |
| 10 | 20 | 200 | 10 |
| 20 | 20 | 400 | 20 |

**No legacy column, on purpose.** An earlier version of this table put
`LegacyAggregator` next to the engine and read "20 vs 400" as a win for the
baseline. That was not a like-for-like comparison: the legacy figure counted
*receive-callback invocations* of `OnSpecification`, the engine figure counted
*completed-aggregate notifications* across every root reachable from the shared
node. Two different events, one column.

**Reading.** Fan-out is linear in N and inherent to the shared-aggregation
graph: an update to a node referenced by N roots re-evaluates and re-notifies
all N, because that is precisely what "this part changed, so these aggregates
changed" means. It is a cost of the shared relation, not an engine defect.

**Why there is no unchanged-republish suppression.** Earlier revisions of this
suite measured a `SuppressUnchangedSnapshots` flag that skipped emission when a
reassembled snapshot matched the previous one. That flag has been removed from
the engine, because on NGVA it cannot work:

- every topic instance carries per-publish metadata — `A_timeOfDataGeneration`
  (NGVA_DM_032) and the `publishingEventID` that identifies each publishing
  event (NGVA_DM_014);
- IDL-generated `Equals`/`GetHashCode` cover **all** attributes, metadata
  included.

So two successive publications of semantically identical data never compare
equal, by construction, and any subscriber-side "is this unchanged?" test
answers "changed" every time. Deciding that nothing meaningful changed requires
knowing which attributes carry meaning — that is the publisher's judgement, not
the aggregation engine's. Attempting it at the subscriber would mean
re-implementing per-topic field selection, which is exactly the per-topic
hand-written logic this engine exists to remove.

## Experiment 2 — steady-state re-publish cost vs. aggregate size (µs / Upsert call)

| Synthetic parts (V) | Legacy | Engine (default) | Engine (+IsolateAggregateBoundaries) |
|---:|---:|---:|---:|
| 0   | 0.120 | 17.81  | 17.45  |
| 10  | 0.411 | 61.18  | 57.14  |
| 50  | 1.339 | 70.95  | 32.77  |
| 100 | 0.680 | 76.11  | 62.56  |
| 200 | 1.176 | 162.13 | 121.35 |
| 400 | 2.096 | 283.52 | 236.09 |
| 800 | 4.403 | 605.27 | 468.86 |

One settled run (`results/exp2_scaling_vs_size.csv`). Across the five settled
runs of `results/exp2_settled_repeats.csv` the V=800 engine median is 605 µs
with a ±5% spread; V=0 is noisier in relative terms (±21%) simply because the
absolute numbers are small. The column that enabled both optional switches at
once was dropped: the paper reports the default configuration and
`IsolateAggregateBoundaries`, so the benchmark now measures exactly those.

**Reading.** Both approaches scale roughly linearly in aggregate size, as
expected (O(V) dictionary work for Legacy; O(V) BFS + snapshot
materialization for the engine).

Because both sides are linear, the overhead is a **constant factor that does
not degrade with size** — it is not the case that large aggregates are
disproportionately punished:

| V | 0 | 10 | 50 | 100 | 200 | 400 | 800 |
|---|--:|--:|--:|--:|--:|--:|--:|
| ratio | 149x | 153x | 53x | 112x | 135x | 136x | 133x |
| legacy, in 0.1 us ticks | 1.2 | 4.3 | 11.2 | 6.8 | 11.4 | 21.9 | 44.5 |

The second row is why V=800 is the point to quote a ratio from, and why the
small-V ratios swing between 53x and 153x: below V=400 the legacy side sits
within a handful of timer ticks, so those ratios are measurement noise, not
signal. Note also that quoting a smaller V would report a *larger* multiple
(149x at V=0) off an unresolvable denominator — V=800 is both the most
reliable and the least unflattering point.

The engine carries a **roughly two-order-of-magnitude constant-factor
overhead** over hand-written dictionary lookups
— the price of the general BFS traversal, `ImmutableHashSet` bookkeeping,
and per-emission `Dictionary`/`List` allocation that the legacy code simply
doesn't do. `IsolateAggregateBoundaries` measurably reduces this overhead
(skips the reciprocal half of bidirectional relations during
Assemble/IsComplete) even though this benchmark's topic model has no
boundary-correctness bug to fix — i.e. it is a real, disclosed performance
lever, not only a correctness one.

## Experiment 3 — completion latency (the Upsert call that completes the aggregate)

5 synthetic parts per mount, 5000 independent aggregates, fresh Stopwatch
per call.

| Config | p50 (µs) | p95 (µs) |
|---|---:|---:|
| Legacy | 0.10 *(= 1 timer tick — at the resolution floor, see EXP5)* | 0.50 *(5 ticks)* |
| Engine (default) | 9.1–10.1 | *not reproducible — do not quote* |

**Reading.** The engine spends roughly **10 µs** completing one aggregate of
this size; the hand-written baseline completes it in *under one timer tick*,
so the harness cannot say how fast it actually is. The honest statement is
therefore an absolute one — "≈10 µs per completed aggregate" — plus the
observation that the baseline is at least two orders of magnitude cheaper.
Quoting a precise multiple from this experiment would be reading precision
into an unresolvable denominator; use EXP2's V=800 figures if a ratio is
needed. The remaining hardening flag does not change per-call cost materially.

## Experiment 4 — order-independence and optional-reference correctness (engine only)

All 3! = 6 arrival orderings of the required {LinearMount, ActualMount,
Specification} topics were fed to a fresh engine instance with no SoftLimits
reference. The subscriber was checked for **exactly one** completion
notification, occurring only after the last required topic arrives.

A separate case declares the standard optional `SoftLimits` reference. It
must produce no completion notification before that target arrives, then
exactly one notification whose snapshot includes SoftLimits.

**Result: 6/6 required orderings pass; referenced optional SoftLimits is held
until arrival (0 notifications before, 1 after).** This verifies the intended
0..1 semantics: absence is valid, while a declared reference remains
incomplete until it resolves.

An earlier version of this benchmark registered
`LinearMount<->ActualMount` as bidirectional without a real reverse
reference, producing 0/24. Reciprocal validation requires a genuine
back-reference and must not be requested for relations whose reverse side is
never populated. The benchmark now registers this generalization
unidirectionally, matching the NGVA IDL, where `C_Actual_Mount` carries no
pointer back to the `C_Linear_Mount` that specializes it.

## Experiment 5 — code-size cost of adding a topic kind (static, not timed)

Concrete example: adding a 7th topic kind, `StreamingStatus`
(per-mount, optional, Composite Aggregation — same shape as
`TargetPosition`), to the 6-kind LinearMount aggregate already implemented in
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
    TryComplete(s.LinearMountSourceId);
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

engine.RegisterBidirectional<LinearMount, StreamingStatus>(
    "LinearMount<->StreamingStatus", mountKind, streamingKind,
    Multiplicity.ZeroOrOne, Multiplicity.One,
    m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
            m.StreamingStatusSourceId.HasValue, m.StreamingStatusSourceId.GetValueOrDefault()),
    s => global::TopicManager.Extensions.AggregationEngine.One(s.LinearMountSourceId));
```
→ **7 new lines, 1 location, 0 existing methods modified.**

(Both approaches equally require adding `StreamingStatusSourceId` to the
`LinearMount` topic class itself — that cost is identical and excluded from the
comparison above.)

**Reading.** For one topic kind the raw line delta (12 vs 7) is modest; the
structural point is qualitative and compounds with N: every Legacy addition
touches the store, the callback, `TryComplete`, and the result type — four
places that must independently stay consistent — while every Engine
addition is a single declarative statement that cannot, by construction,
leave `TryComplete`-equivalent logic out of sync, because there is no
per-kind `TryComplete` to forget.

## Experiment 6 — timer resolution (is the EXP3 baseline measurable at all?)

Added after noticing EXP3's legacy row came out at exactly 0.100 / 0.500 µs
on every single run.

```
Stopwatch.Frequency        : 10,000,000 Hz
one tick                   : 0.1000 µs
Stopwatch.IsHighResolution : True
EXP3 legacy p50 (0.100 µs) : 1.00 tick
EXP3 legacy p95 (0.500 µs) : 5.00 ticks
EXP3 engine p50 (~10 µs)   : 100 ticks
empty Restart/Stop pair    : p50 0.0000 µs, p95 0.1000 µs
```

**Reading.** Confirmed: the legacy baseline in EXP3 is one timer tick. The
harness physically cannot distinguish 0.1 µs from 0.01 µs, so the legacy
figure is an upper bound and any EXP3-derived "N× slower" is a *lower*
bound on the real multiple — it understates the gap rather than
flattering it. EXP2 is unaffected (both sides are tens to thousands of
ticks there).

## Experiment 7 — sustained throughput (attempted, not usable)

Intended to give a more meaningful figure than a ratio between two
sub-millisecond numbers: completed aggregates per second, measured as a
sustained loop rather than inferred from latency.

**It did not produce a quotable number.** Engine (default), consecutive
runs: 13,620 / 17,921 / 30,765 / 16,488 aggregates/sec — a 2.3× spread,
trending upward within a session, which points at JIT tiering, CPU boost
state, and a growing store rather than a settled steady state. Kept in the
repo because the harness is reusable and the instability is itself worth
recording, but **do not cite these numbers**.

## Honest limitations of this benchmark

- Single machine, in-process, synthetic topic stream — no DDS transport,
  serialization, or network latency is included on either side; both
  approaches are measured from the same in-memory `Upsert`/`OnXxx` call.
- `LegacyAggregator` is a fair-effort baseline (it does maintain a
  hand-written reverse index for the shared Specification, matching what a
  competent implementation would do), not a strawman with the reverse index
  omitted.
- **Timing runs must be spaced out.** See the reproducibility section at the
  top: back-to-back runs inflate every timing figure by ~2× on this machine.
  All timing numbers quoted here are from thermally settled runs.
- Experiment 2/3 absolute microsecond figures depend on machine/JIT state;
  the paper should report them as *this measurement*, not as a
  universal constant, and disclose the methodology above.
- Of the seven experiments, only **1, 4, and 5** are fully deterministic
  (notification counts, permutation coverage, static line counts). 2 and 3
  (p50) are reproducible within ~10% under the stated conditions. 3 (p95)
  and 7 are not reproducible and are excluded from any claim.
