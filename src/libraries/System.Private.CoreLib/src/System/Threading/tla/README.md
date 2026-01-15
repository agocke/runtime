# TLA+ Models for ThreadPool Work Queue

This directory contains TLA+ specifications for formally verifying the
ThreadPool work queue's synchronization protocol.

## Overview

The core challenge in the ThreadPool is ensuring **work items are never stranded**
in the queue with no worker threads coming to process them. This requires careful
coordination between:

- **Enqueuers**: Threads adding work to the queue
- **Workers**: Threads processing work from the queue
- **The synchronization flag**: `_hasOutstandingThreadRequest`

## Directory Structure

```
tla/
├── ThreadPoolWorkQueue.tla    # Main model - evolves with the code
├── ThreadPoolWorkQueue.cfg    # TLC configuration
├── README.md                  # This file
└── examples/
    └── pr-121887/             # Bug fixed in PR #121887
        ├── README.md          # Detailed explanation
        ├── BuggyModel.tla     # 3-state buggy implementation
        ├── BuggyModel.cfg
        ├── FixedModel.tla     # 2-state fixed implementation
        └── FixedModel.cfg
```

## Quick Start

### Prerequisites

**Option 1: VS Code Extension (Recommended)**
1. Install [TLA+ extension](https://marketplace.visualstudio.com/items?itemName=alygin.vscode-tlaplus)
2. The extension includes TLC model checker

**Option 2: Command Line**
1. Install Java Runtime (JRE 11+)
2. Download TLA+ tools from https://github.com/tlaplus/tlaplus/releases
3. Add `tla2tools.jar` to your PATH or use full path

### Running the Model Checker

**VS Code:**
1. Open `ThreadPoolWorkQueue.tla`
2. Press `Ctrl+Shift+P` (or `Cmd+Shift+P` on Mac)
3. Run "TLA+: Check model with TLC"
4. View results in the TLA+ output panel

**Command Line:**
```bash
# From this directory
tlc ThreadPoolWorkQueue.tla -config ThreadPoolWorkQueue.cfg

# Or with explicit path to tla2tools.jar
java -jar /path/to/tla2tools.jar ThreadPoolWorkQueue.tla -config ThreadPoolWorkQueue.cfg
```

### Expected Output

For the main model (fixed implementation):
```
TLC2 Version 2.xx ...
Running breadth-first search...
Model checking completed. No error has been found.
  Distinct states found: XX
```

For the buggy example (`examples/pr-121887/BuggyModel.tla`):
```
Error: Invariant NoStrandedWork is violated.
The following sequence of states leads to the violation:
...
```

## The Main Model

`ThreadPoolWorkQueue.tla` models the current implementation with these key elements:

### Variables
| TLA+ Variable | C# Field | Description |
|---------------|----------|-------------|
| `queueSize` | `workItems`, etc. | Abstract count of all work items |
| `hasOutstandingThreadRequest` | `_hasOutstandingThreadRequest` | Synchronization flag |
| `workersRequested` | Pending `RequestWorkerThread()` | Worker requests in flight |

### Key Operations
| TLA+ Action | C# Method | Location |
|-------------|-----------|----------|
| `EnqueueStart` | `queue.Enqueue()` | Line ~640 |
| `EnqueueEnsureRequested` | `EnsureThreadRequested()` | Lines 612-618 |
| `WorkerClearFlagAndCheck` | Flag clear + barrier | Lines 944-947 |
| `WorkerFindWork` | `Dequeue()` returns item | Line 949 |
| `WorkerEnsureAnotherRequested` | `EnsureThreadRequested()` | Line 975 |

### Safety Property

```tla
NoStrandedWork ==
    (queueSize > 0 /\ ~EnqueueInProgress) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ hasOutstandingThreadRequest = 1
```

This states: if there's work and no enqueue is in progress, either:
- A worker is active (will check the queue), or
- A worker has been requested (will start and check), or
- The flag guarantees someone will check

## Modifying the Model

When changing `ThreadPoolWorkQueue.cs`, consider updating the model:

1. **New queue types**: Update `queueSize` abstraction if adding new queues
2. **Protocol changes**: Update actions to match new synchronization logic
3. **New operations**: Add new TLA+ actions for significant new code paths

Run TLC after changes to verify the safety property still holds.

## Advanced Usage

### Checking Liveness

To verify that all work eventually gets processed:

1. Edit `ThreadPoolWorkQueue.cfg`
2. Comment out `NEXT Next`
3. Uncomment `SPECIFICATION Spec` and `PROPERTY AllWorkEventuallyProcessed`
4. Run TLC (this is slower due to temporal logic checking)

### Increasing State Space

For more thorough checking, increase constants in `.cfg`:
```
CONSTANTS
    MaxQueueSize = 3
    NumEnqueuers = 2
    NumWorkers = 2
```

Note: State space grows exponentially. Start small.

### Debugging Violations

When TLC finds a violation:
1. Examine the state trace in the output
2. Each state shows all variable values
3. Identify which action led to the bad state
4. Map back to C# code using comments in the TLA+ file

## Related Resources

- [TLA+ Video Course](https://lamport.azurewebsites.net/video/videos.html) by Leslie Lamport
- [Learn TLA+](https://learntla.com/) - Interactive tutorial
- [PR #121887](https://github.com/dotnet/runtime/pull/121887) - The fix this model verifies
- [#121608](https://github.com/dotnet/runtime/issues/121608) - Original bug report
