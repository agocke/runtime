------------------------ MODULE ThreadPoolWorkQueue ------------------------
(***************************************************************************)
(* TLA+ Model of the ThreadPool Work Queue Thread Request Mechanism        *)
(*                                                                         *)
(* This is the canonical model of ThreadPoolWorkQueue.cs and is intended   *)
(* to evolve alongside the code. It models the core synchronization        *)
(* protocol that ensures work items are never stranded in the queue.       *)
(*                                                                         *)
(* CORRESPONDING SOURCE FILE:                                              *)
(*   src/libraries/System.Private.CoreLib/src/System/Threading/            *)
(*   ThreadPoolWorkQueue.cs                                                *)
(*                                                                         *)
(* LAST UPDATED: January 2026 (post PR #121887)                            *)
(***************************************************************************)

EXTENDS Integers, Sequences, FiniteSets

(***************************************************************************)
(* CONSTANTS - Model parameters                                            *)
(***************************************************************************)
CONSTANTS
    MaxQueueSize,     \* Maximum items that can be in the queue
    NumEnqueuers,     \* Number of enqueuer threads
    NumWorkers        \* Number of worker threads

(***************************************************************************)
(* VARIABLES                                                               *)
(*                                                                         *)
(* Mapping to C# fields in ThreadPoolWorkQueue.cs:                         *)
(*                                                                         *)
(* queueSize:                                                              *)
(*   Abstraction of all work item queues:                                  *)
(*   - workItems (ConcurrentQueue<object>, line ~416)                      *)
(*   - highPriorityWorkItems (line ~417)                                   *)
(*   - WorkStealingQueue per thread (lines 98-392)                         *)
(*   - _assignableWorkItemQueues (line ~422)                               *)
(*                                                                         *)
(* hasOutstandingThreadRequest:                                            *)
(*   Maps directly to: CacheLineSeparated._hasOutstandingThreadRequest     *)
(*   C# location: line ~450                                                *)
(*   Semantics (from C# comments, lines 440-449):                          *)
(*     0: has no guarantees                                                *)
(*     1: a worker will check work queues and ensure that any work items   *)
(*        inserted before setting the flag are picked up                   *)
(*                                                                         *)
(* workersRequested:                                                       *)
(*   Abstraction of pending ThreadPool.RequestWorkerThread() calls         *)
(*   C# location: EnsureThreadRequested() calls RequestWorkerThread()      *)
(*   at line ~617                                                          *)
(***************************************************************************)
VARIABLES
    queueSize,                   \* Abstraction of work item count
    hasOutstandingThreadRequest, \* The synchronization flag (0 or 1)
    enqueuers,                   \* State of each enqueuer thread
    workers,                     \* State of each worker thread
    workersRequested             \* Pending worker thread requests

vars == <<queueSize, hasOutstandingThreadRequest, enqueuers, workers, workersRequested>>

(***************************************************************************)
(* WORKER STATES                                                           *)
(*                                                                         *)
(* Maps to code locations in Dispatch() method (line ~933):                *)
(*                                                                         *)
(* "idle"       - Worker not in Dispatch() method                          *)
(* "starting"   - Entered Dispatch(), before clearing flag (line ~944)     *)
(* "checking"   - After flag cleared + memory barrier, checking queue      *)
(*                C#: lines 944-949                                        *)
(* "found_work" - Dequeued item, about to ensure another request           *)
(*                C#: after line 949, workItem != null                     *)
(* "processing" - Executing the work item callback                         *)
(*                C#: inside while loop, line ~996+                        *)
(***************************************************************************)
WorkerStates == {"idle", "starting", "checking", "found_work", "processing"}

(***************************************************************************)
(* TYPE INVARIANT                                                          *)
(***************************************************************************)
TypeOK ==
    /\ queueSize \in 0..MaxQueueSize
    /\ hasOutstandingThreadRequest \in {0, 1}
    /\ enqueuers \in [1..NumEnqueuers -> {"idle", "enqueuing"}]
    /\ workers \in [1..NumWorkers -> WorkerStates]
    /\ workersRequested \in 0..NumWorkers

(***************************************************************************)
(* INITIAL STATE                                                           *)
(***************************************************************************)
Init ==
    /\ queueSize = 0
    /\ hasOutstandingThreadRequest = 0
    /\ enqueuers = [e \in 1..NumEnqueuers |-> "idle"]
    /\ workers = [w \in 1..NumWorkers |-> "idle"]
    /\ workersRequested = 0

(***************************************************************************)
(* ENQUEUE OPERATIONS                                                      *)
(*                                                                         *)
(* Models ThreadPoolWorkQueue.Enqueue() at lines 619-651:                  *)
(*                                                                         *)
(*   public void Enqueue(object callback, bool forceGlobal)                *)
(*   {                                                                     *)
(*       // ... choose queue based on forceGlobal and thread-local ...     *)
(*       queue.Enqueue(callback);        // <-- EnqueueStart               *)
(*       EnsureThreadRequested();        // <-- EnqueueEnsureRequested     *)
(*   }                                                                     *)
(***************************************************************************)

\* Step 1: Add item to queue (before calling EnsureThreadRequested)
EnqueueStart(e) ==
    /\ enqueuers[e] = "idle"
    /\ queueSize < MaxQueueSize
    /\ queueSize' = queueSize + 1
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "enqueuing"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, workers, workersRequested>>

(***************************************************************************)
(* EnsureThreadRequested() - lines 612-618                                 *)
(*                                                                         *)
(*   internal void EnsureThreadRequested()                                 *)
(*   {                                                                     *)
(*       if (Interlocked.Exchange(ref _hasOutstandingThreadRequest, 1) == 0)*)
(*       {                                                                 *)
(*           ThreadPool.RequestWorkerThread();                             *)
(*       }                                                                 *)
(*   }                                                                     *)
(*                                                                         *)
(* Interlocked.Exchange atomically: reads old value, writes 1, returns old *)
(* Only if old value was 0 do we actually request a worker thread.         *)
(***************************************************************************)
EnqueueEnsureRequested(e) ==
    /\ enqueuers[e] = "enqueuing"
    /\ LET oldValue == hasOutstandingThreadRequest
       IN /\ hasOutstandingThreadRequest' = 1
          /\ IF oldValue = 0
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "idle"]
    /\ UNCHANGED <<queueSize, workers>>

(***************************************************************************)
(* WORKER OPERATIONS                                                       *)
(*                                                                         *)
(* Models ThreadPoolWorkQueue.Dispatch() at lines 933-1115                 *)
(***************************************************************************)

\* Worker thread starts (responds to RequestWorkerThread)
\* Platform-specific: see ThreadPool.Unix.cs or ThreadPool.Windows.cs
WorkerStart(w) ==
    /\ workers[w] = "idle"
    /\ workersRequested > 0
    /\ workersRequested' = workersRequested - 1
    /\ workers' = [workers EXCEPT ![w] = "starting"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers>>

(***************************************************************************)
(* THE KEY OPERATION: Clear flag BEFORE checking queue                     *)
(*                                                                         *)
(* C# lines 944-947:                                                       *)
(*   // Before dequeuing the first work item, acknowledge that the thread  *)
(*   // request has been satisfied                                         *)
(*   workQueue._separated._hasOutstandingThreadRequest = 0;                *)
(*                                                                         *)
(*   // The state change must happen before sweeping queues for items.     *)
(*   Interlocked.MemoryBarrier();                                          *)
(*                                                                         *)
(* This ordering is CRITICAL for correctness:                              *)
(*   1. Clear flag to 0                                                    *)
(*   2. Memory barrier (ensures visibility)                                *)
(*   3. Check queue for work                                               *)
(*                                                                         *)
(* Any enqueue that happens AFTER step 1 will see flag=0 and request       *)
(* a new worker. The barrier ensures this relationship holds.              *)
(***************************************************************************)
WorkerClearFlagAndCheck(w) ==
    /\ workers[w] = "starting"
    /\ hasOutstandingThreadRequest' = 0
    /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

\* Worker finds work in queue (lines 949+, workItem != null)
WorkerFindWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize > 0
    /\ queueSize' = queueSize - 1
    /\ workers' = [workers EXCEPT ![w] = "found_work"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, enqueuers, workersRequested>>

\* Worker finds no work - returns from Dispatch (lines 951-966)
\* Any concurrent enqueue will have seen flag=0 and requested a worker
WorkerFindNoWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize = 0
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker ensures another thread requested before processing               *)
(*                                                                         *)
(* C# lines 970-975:                                                       *)
(*   // The workitems currently in the queues could have asked only for    *)
(*   // one worker. We are going to process a workitem, which may take     *)
(*   // unknown time or even block. We must ensure at least one more       *)
(*   // worker is coming if the queue is not empty.                        *)
(*   workQueue.EnsureThreadRequested();                                    *)
(***************************************************************************)
WorkerEnsureAnotherRequested(w) ==
    /\ workers[w] = "found_work"
    /\ LET oldValue == hasOutstandingThreadRequest
       IN /\ hasOutstandingThreadRequest' = 1
          /\ IF oldValue = 0
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ workers' = [workers EXCEPT ![w] = "processing"]
    /\ UNCHANGED <<queueSize, enqueuers>>

\* Worker finishes processing work item
WorkerFinishProcessing(w) ==
    /\ workers[w] = "processing"
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested>>

(***************************************************************************)
(* STATE MACHINE                                                           *)
(***************************************************************************)
Next ==
    \/ \E e \in 1..NumEnqueuers:
        \/ EnqueueStart(e)
        \/ EnqueueEnsureRequested(e)
    \/ \E w \in 1..NumWorkers:
        \/ WorkerStart(w)
        \/ WorkerClearFlagAndCheck(w)
        \/ WorkerFindWork(w)
        \/ WorkerFindNoWork(w)
        \/ WorkerEnsureAnotherRequested(w)
        \/ WorkerFinishProcessing(w)

(***************************************************************************)
(* FAIRNESS                                                                *)
(*                                                                         *)
(* Weak fairness ensures threads eventually make progress.                 *)
(***************************************************************************)
Fairness ==
    /\ \A e \in 1..NumEnqueuers:
        /\ WF_vars(EnqueueStart(e))
        /\ WF_vars(EnqueueEnsureRequested(e))
    /\ \A w \in 1..NumWorkers:
        /\ WF_vars(WorkerStart(w))
        /\ WF_vars(WorkerClearFlagAndCheck(w))
        /\ WF_vars(WorkerFindWork(w))
        /\ WF_vars(WorkerFindNoWork(w))
        /\ WF_vars(WorkerEnsureAnotherRequested(w))
        /\ WF_vars(WorkerFinishProcessing(w))

Spec == Init /\ [][Next]_vars /\ Fairness

(***************************************************************************)
(* SAFETY PROPERTIES                                                       *)
(***************************************************************************)

ActiveWorkers == {w \in 1..NumWorkers: workers[w] # "idle"}
EnqueueInProgress == \E e \in 1..NumEnqueuers: enqueuers[e] # "idle"

(***************************************************************************)
(* NoStrandedWork: The core safety property                                *)
(*                                                                         *)
(* Work items must never be "stranded" - left in queue with no worker      *)
(* coming to process them. If there's work AND no enqueue in progress:     *)
(*   - Either a worker is active (will check queue), OR                    *)
(*   - A worker has been requested (will start and check), OR              *)
(*   - The flag is set (guarantees someone will check)                     *)
(***************************************************************************)
NoStrandedWork ==
    (queueSize > 0 /\ ~EnqueueInProgress) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ hasOutstandingThreadRequest = 1

(***************************************************************************)
(* LIVENESS PROPERTY                                                       *)
(***************************************************************************)

\* All work eventually gets processed (requires fairness)
AllWorkEventuallyProcessed == [](queueSize > 0 => <>(queueSize = 0))

=============================================================================
