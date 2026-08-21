#!/bin/bash
# Drives Experiment 2 one aggregate size per process.
#
# A single process that sweeps every size lets the heap grow across ~100
# measurement blocks; under server GC the sizes measured last then read
# 1.5-2x high. One process per size keeps every size on the same footing.
# The first run after a build is discarded (tiered JIT still promoting), and
# runs are spaced so the CPU is not measuring its own thermal throttling.
set -e
cd "$(dirname "$0")"
EXE=bin/Release/net8.0/AggregationEngine.Benchmarks.exe
COOLDOWN=${COOLDOWN:-90}

rm -f results/exp2_raw_repetitions.csv
sleep "$COOLDOWN"
"$EXE" --exp2 --size 0 > /dev/null 2>&1   # discarded: post-build run
rm -f results/exp2_raw_repetitions.csv

for v in 0 10 50 100 200 400 800; do
  sleep "$COOLDOWN"
  "$EXE" --exp2 --size "$v"
done

echo
echo "=== medians ==="
"$EXE" --exp2 --summarize
