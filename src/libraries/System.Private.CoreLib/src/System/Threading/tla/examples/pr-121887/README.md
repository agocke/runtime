# PR #121887: ThreadPool Work Item Stranding Bug

This directory contains the TLA+ models and documentation for the bug fixed in
[PR #121887](https://github.com/dotnet/runtime/pull/121887).

## The Bug

Work items could become **stranded** in the ThreadPool queue with no worker threads
scheduled to process them, leading to deadlocks. This was reported in:
- [#121608](https://github.com/dotnet/runtime/issues/121608) - Windows IO completion
- [#119043](https://github.com/dotnet/runtime/issues/119043) - General ThreadPool

## Root Cause

The original implementation used a **3-state protocol** for coordinating between
enqueuers and workers:

```
NotScheduled  ──(enqueue)──►  Scheduled  ──(worker starts)──►  Determining
      ▲                                                              │
      └──────────────────(CAS if no work found)──────────────────────┘
```

The race condition:
1. Worker sets state to `Determining` (signaling "I'm about to check")
2. Worker checks queue, finds nothing (or misses due to timing)
3. Enqueuer adds work, sees state is `Determining` or `Scheduled`
4. Enqueuer does NOT request a worker (assumes existing worker handles it)
5. Worker CAS succeeds: `Determining` → `NotScheduled`
6. Worker exits
7. **DEADLOCK**: Work in queue, state is `NotScheduled`, no worker requested!

## The Fix

Simplified to a **2-state boolean flag** with a critical ordering change:

```
┌─────────────────────────────────────────────────────────────┐
│  WORKER: Clear flag FIRST, then check queue                 │
│                                                             │
│    _hasOutstandingThreadRequest = 0;   // Clear flag        │
│    Interlocked.MemoryBarrier();        // Ensure visibility │
│    workItem = Dequeue();               // Check queue       │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│  ENQUEUER: Add work, then set flag (requesting if was 0)    │
│                                                             │
│    queue.Enqueue(item);                // Add work          │
│    if (Interlocked.Exchange(ref flag, 1) == 0)              │
│        RequestWorkerThread();          // Request worker    │
└─────────────────────────────────────────────────────────────┘
```

**Why this works:**
- If enqueue happens BEFORE worker clears flag → Worker sees the work
- If enqueue happens AFTER worker clears flag → Enqueue sees flag=0, requests worker

Either way, the work gets processed. There's no window where both sides can
"miss" each other.

## Code Changes

### Before (Buggy) - Simplified

```csharp
// Worker side (buggy)
void Dispatch()
{
    // Set state to Determining BEFORE checking
    var oldStage = Interlocked.Exchange(ref _stage, Determining);

    Interlocked.MemoryBarrier();

    var workItem = Dequeue();
    if (workItem == null)
    {
        // CAS: Determining -> NotScheduled
        Interlocked.CompareExchange(ref _stage, NotScheduled, Determining);
        return;  // BUG: May exit with work in queue!
    }
    // ... process work
}

// Enqueuer side (buggy)
void Enqueue(object item)
{
    queue.Enqueue(item);

    // Only request if NotScheduled
    var oldStage = Interlocked.Exchange(ref _stage, Scheduled);
    if (oldStage == NotScheduled)
    {
        RequestWorkerThread();  // BUG: May skip if Determining/Scheduled!
    }
}
```

### After (Fixed)

```csharp
// Worker side (fixed) - ThreadPoolWorkQueue.cs lines 944-975
void Dispatch()
{
    // Clear flag BEFORE checking queue - THE KEY FIX
    _hasOutstandingThreadRequest = 0;
    Interlocked.MemoryBarrier();

    var workItem = Dequeue();
    if (workItem == null)
    {
        return;  // Safe: any concurrent enqueue will request a worker
    }

    // Ensure another worker for remaining items
    EnsureThreadRequested();
    // ... process work
}

// Enqueuer side (fixed) - ThreadPoolWorkQueue.cs lines 612-651
void Enqueue(object item)
{
    queue.Enqueue(item);
    EnsureThreadRequested();
}

void EnsureThreadRequested()
{
    // Always set flag to 1; only request if was 0
    if (Interlocked.Exchange(ref _hasOutstandingThreadRequest, 1) == 0)
    {
        RequestWorkerThread();
    }
}
```

## TLA+ Models

### BuggyModel.tla

Models the **original 3-state protocol**. TLC finds a counterexample showing
exactly how work becomes stranded:

```
TLC found a counterexample showing work stranding:
State 1: queue=0, stage=NotScheduled, worker=idle
State 2: queue=1, stage=Scheduled, worker=idle (enqueue + request)
State 3: queue=1, stage=Determining, worker=checking
State 4: queue=1, stage=Determining, worker=found_nothing (missed the work!)
State 5: queue=1, stage=NotScheduled, worker=idle (CAS succeeded)
VIOLATED: Work in queue, no worker scheduled!
```

### FixedModel.tla

Models the **fixed 2-state protocol**. TLC verifies that the safety property
`NoStrandedWork` holds for all reachable states.

```
Model checking completed. No error has been found.
Distinct states: 43
```

## Running the Models

```bash
# Check the buggy model (should find violation)
cd examples/pr-121887
tlc BuggyModel.tla -config BuggyModel.cfg

# Check the fixed model (should pass)
tlc FixedModel.tla -config FixedModel.cfg
```

Or use VS Code with the TLA+ extension:
1. Open a `.tla` file
2. Press `Ctrl+Shift+P` → "TLA+: Check model with TLC"

## Key Insight

The fundamental issue was the **order of operations**:

| Operation Order | Result |
|-----------------|--------|
| Check queue → Clear flag | ❌ Window for race |
| Clear flag → Check queue | ✅ No race possible |

By clearing the flag *before* checking the queue, we ensure that any enqueue
that happens concurrently will either:
1. Be visible to our queue check (if it happened before we cleared), OR
2. See our cleared flag and request a worker (if it happened after)

This is a classic happens-before relationship enforced by the memory barrier.
