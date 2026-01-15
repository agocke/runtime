# TLA+ Model for ThreadPool Work Queue

This directory contains TLA+ specifications for modeling the ThreadPool work queue's
worker thread request mechanism. The goal is to demonstrate how formal verification
can catch concurrency bugs like the one fixed in PR #121887.

## Background

PR [#121887](https://github.com/dotnet/runtime/pull/121887) fixed a reliability bug
in the ThreadPool where work items could be left stranded in the queue with no worker
threads coming to process them. This led to deadlocks reported in:
- Windows IO completion: #121608
- General purpose ThreadPool: #119043

## The Bug

The original implementation used a 3-state scheme:
- `NotScheduled` → `Scheduled` (when work is enqueued)
- `Scheduled` → `Determining` (before dequeuing)
- `Determining` → `NotScheduled` (if queue empty) or `Scheduled` (if more work)

The race condition occurred when:
1. Worker sets state to `Determining` and checks the queue
2. Worker finds no work (or misses the item due to timing)
3. Worker CAS from `Determining` to `NotScheduled` succeeds
4. Meanwhile, enqueuer added work and saw state was `Determining` or `Scheduled`
5. Enqueuer did NOT request a new worker (assuming existing worker would handle it)
6. Result: Work item in queue, no worker scheduled to process it → **DEADLOCK**

## The Fix

The fix simplified to a 2-state boolean flag (`_hasOutstandingThreadRequest`):
- Worker clears the flag BEFORE checking for work
- Enqueuer always requests a worker if flag is 0
- If work was added between flag clear and check, worker will see it
- If worker misses it, the enqueuer will have requested another worker

## TLC Model Checker Results

### Buggy Model (`ThreadPoolRace.tla`)
```
Invariant WorkNotStranded is violated.
```
TLC found a **10-step counterexample** showing exactly how work becomes stranded:
1. Enqueuer adds work, sets stage to `Scheduled`, requests worker
2. Worker starts, sets stage to `Determining`
3. Worker checks queue, finds nothing (timing/race)
4. Worker CAS succeeds: `Determining` → `NotScheduled`
5. Worker exits
6. **Final state**: `queue = 1`, `stage = NotScheduled`, no worker requested!

### Fixed Model (`ThreadPoolFixed.tla`)
```
Model checking completed. No error has been found.
78 states generated, 43 distinct states found.
```

## Files

- `ThreadPoolRace.tla` - Minimal model demonstrating the exact race condition
- `ThreadPoolRace.cfg` - TLC configuration for the race model
- `ThreadPoolBuggy.tla` - More detailed model of the buggy 3-state implementation
- `ThreadPoolBuggy.cfg` - TLC configuration for the buggy model
- `ThreadPoolFixed.tla` - Model of the fixed 2-state implementation
- `ThreadPoolFixed.cfg` - TLC configuration for the fixed model

## Running the Models

Using VS Code with the TLA+ extension:
1. Open a `.tla` file
2. Press `Ctrl+Shift+P` and run "TLA+: Check model with TLC"
3. Or use the TLA+ tools from command line

## Key Insight

The fundamental issue was the **order of operations**:

**Buggy**: Set state → Memory barrier → Check queue
- Window exists where enqueuer sees "active" state but worker exits

**Fixed**: Clear flag → Memory barrier → Check queue  
- Any concurrent enqueue will see flag=0 and request a worker
- Worker will either see the work OR enqueuer will have requested another worker

This is a classic example where formal verification can catch subtle concurrency bugs
that are extremely difficult to reproduce and debug in production.
