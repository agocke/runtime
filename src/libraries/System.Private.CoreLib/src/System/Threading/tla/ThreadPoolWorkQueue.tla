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
(*                                                                         *)
(* MEMORY MODEL NOTE:                                                      *)
(*   TLA+ assumes sequential consistency by default. The real C# code      *)
(*   uses Interlocked.MemoryBarrier() (line 947) to establish happens-     *)
(*   before relationships between flag operations and queue checks.        *)
(*   This model implicitly captures the intended semantics because:        *)
(*   1. Each TLA+ action is atomic                                         *)
(*   2. Variable reads/writes within an action see consistent state        *)
(*   The key insight is that the ORDERING of operations (clear flag        *)
(*   BEFORE checking queue) is what matters, and TLA+ correctly models     *)
(*   this via the sequencing of actions.                                   *)
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
    workersRequested,            \* Pending worker thread requests
    enqueuingStopped             \* TRUE when no more work will be enqueued (for liveness)

vars == <<queueSize, hasOutstandingThreadRequest, enqueuers, workers, workersRequested, enqueuingStopped>>

(***************************************************************************)
(* WORKER STATES                                                           *)
(*                                                                         *)
(* Maps to code locations in Dispatch() method (line ~933):                *)
(*                                                                         *)
(* "idle"       - Worker not in Dispatch() method                          *)
(*                                                                         *)
(* "starting"   - Entered Dispatch(), before clearing flag (line ~944)     *)
(*                                                                         *)
(* "checking"   - After flag cleared + memory barrier, checking queue      *)
(*                C#: lines 944-949                                        *)
(*                                                                         *)
(* "found_work" - Dequeued item, about to ensure another request           *)
(*                C#: after line 949/1004, workItem != null                *)
(*                                                                         *)
(* "processing" - Executing the work item callback                         *)
(*                C#: inside while loop, line ~996+                        *)
(*                                                                         *)
(* "looping"    - Finished one item, checking for more (worker loop)       *)
(*                C#: line ~999, continuing while(true) loop               *)
(*                Models that workers process multiple items per dispatch  *)
(***************************************************************************)
WorkerStates == {"idle", "starting", "checking", "found_work", "processing", "looping"}

(***************************************************************************)
(* TYPE INVARIANT                                                          *)
(***************************************************************************)
TypeOK ==
    /\ queueSize \in 0..MaxQueueSize
    /\ hasOutstandingThreadRequest \in {0, 1}
    /\ enqueuers \in [1..NumEnqueuers -> {"idle", "enqueuing"}]
    /\ workers \in [1..NumWorkers -> WorkerStates]
    /\ workersRequested \in 0..NumWorkers
    /\ enqueuingStopped \in BOOLEAN

(***************************************************************************)
(* INITIAL STATE                                                           *)
(***************************************************************************)
Init ==
    /\ queueSize = 0
    /\ hasOutstandingThreadRequest = 0
    /\ enqueuers = [e \in 1..NumEnqueuers |-> "idle"]
    /\ workers = [w \in 1..NumWorkers |-> "idle"]
    /\ workersRequested = 0
    /\ enqueuingStopped = FALSE

(***************************************************************************)
(* ENQUEUE OPERATIONS                                                      *)
(*                                                                         *)
(* Models ThreadPoolWorkQueue.Enqueue() at lines 621-651:                  *)
(*                                                                         *)
(*   public void Enqueue(object callback, bool forceGlobal)                *)
(*   {                                                                     *)
(*       // ... choose queue based on forceGlobal and thread-local ...     *)
(*       queue.Enqueue(callback);        // <-- EnqueueStart               *)
(*       EnsureThreadRequested();        // <-- EnqueueEnsureRequested     *)
(*   }                                                                     *)
(***************************************************************************)

\* Step 1: Add item to queue (before calling EnsureThreadRequested)
\* Disabled when enqueuingStopped = TRUE (for closed system liveness checking)
EnqueueStart(e) ==
    /\ enqueuers[e] = "idle"
    /\ ~enqueuingStopped
    /\ queueSize < MaxQueueSize
    /\ queueSize' = queueSize + 1
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "enqueuing"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, workers, workersRequested, enqueuingStopped>>

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
    /\ UNCHANGED <<queueSize, workers, enqueuingStopped>>

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
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, enqueuingStopped>>

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
(*                                                                         *)
(* MEMORY BARRIER SEMANTICS:                                               *)
(*   The Interlocked.MemoryBarrier() creates a full fence ensuring:        *)
(*   - All writes before the barrier are visible to other threads          *)
(*   - All reads after the barrier see the latest values                   *)
(*   In TLA+, this is modeled by the atomic transition: the flag write     *)
(*   and subsequent queue read happen in a well-defined order that other   *)
(*   threads observe consistently.                                         *)
(***************************************************************************)
WorkerClearFlagAndCheck(w) ==
    /\ workers[w] = "starting"
    /\ hasOutstandingThreadRequest' = 0
    /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested, enqueuingStopped>>

(***************************************************************************)
(* Worker dequeue operations                                               *)
(*                                                                         *)
(* Models Dequeue() at lines 758-843, which checks:                        *)
(*   1. Local work-stealing queue (LocalPop)                               *)
(*   2. High-priority queue                                                *)
(*   3. Assigned global queue                                              *)
(*   4. Main global queue                                                  *)
(*   5. Other assignable queues                                            *)
(*   6. Other threads' work-stealing queues (TrySteal)                     *)
(***************************************************************************)

\* Worker finds work in queue (lines 949+, workItem != null)
WorkerFindWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize > 0
    /\ queueSize' = queueSize - 1
    /\ workers' = [workers EXCEPT ![w] = "found_work"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, enqueuers, workersRequested, enqueuingStopped>>

(***************************************************************************)
(* WORK STEALING AND MISSED STEAL                                          *)
(*                                                                         *)
(* Models TrySteal() at lines 327-370. Work stealing can FAIL to acquire   *)
(* the lock on another thread's queue, setting missedSteal = true.         *)
(*                                                                         *)
(* C# lines 327-370 (TrySteal):                                            *)
(*   public object? TrySteal(ref bool missedSteal)                         *)
(*   {                                                                     *)
(*       while (true)                                                      *)
(*       {                                                                 *)
(*           if (CanSteal)                                                 *)
(*           {                                                             *)
(*               bool taken = false;                                       *)
(*               try                                                       *)
(*               {                                                         *)
(*                   m_foreignLock.TryEnter(ref taken);                    *)
(*                   if (taken)                                            *)
(*                   {                                                     *)
(*                       // ... steal logic ...                            *)
(*                   }                                                     *)
(*               }                                                         *)
(*               finally { if (taken) m_foreignLock.Exit(...); }           *)
(*               missedSteal = true;  // <-- Lock contention!              *)
(*           }                                                             *)
(*           return null;                                                  *)
(*       }                                                                 *)
(*   }                                                                     *)
(*                                                                         *)
(* When missedSteal is true, Dispatch calls EnsureThreadRequested():       *)
(*                                                                         *)
(* C# lines 961-963 and 1020-1022:                                         *)
(*   if (missedSteal)                                                      *)
(*   {                                                                     *)
(*       workQueue.EnsureThreadRequested();                                *)
(*   }                                                                     *)
(*                                                                         *)
(* This is a CRITICAL safety mechanism: if we couldn't steal due to        *)
(* lock contention, there might be work we couldn't see. Request another   *)
(* worker to ensure that work doesn't get stranded.                        *)
(***************************************************************************)

\* Worker checks queue but misses work due to steal contention
\* This can happen even if queueSize > 0 - models the race condition
\* where TrySteal fails to acquire the lock (missedSteal = true)
WorkerMissedSteal(w) ==
    /\ workers[w] = "checking"
    /\ queueSize > 0  \* Work exists but we couldn't get it
    \* We must request another worker since we might have missed items
    /\ LET oldValue == hasOutstandingThreadRequest
       IN /\ hasOutstandingThreadRequest' = 1
          /\ IF oldValue = 0
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, enqueuers, enqueuingStopped>>

\* Worker finds no work - returns from Dispatch (lines 951-966)
\* Any concurrent enqueue will have seen flag=0 and requested a worker
WorkerFindNoWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize = 0
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested, enqueuingStopped>>

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
    /\ UNCHANGED <<queueSize, enqueuers, enqueuingStopped>>

(***************************************************************************)
(* WORKER LOOP - Multiple work items per dispatch                          *)
(*                                                                         *)
(* Real workers loop in Dispatch() processing multiple items until:        *)
(*   - Queue is empty                                                      *)
(*   - Quantum expires (~30ms, DispatchQuantumMs)                          *)
(*   - Hill climbing requests thread to park                               *)
(*                                                                         *)
(* C# lines 999-1050 (while loop):                                         *)
(*   while (true)                                                          *)
(*   {                                                                     *)
(*       // ... process workItem ...                                       *)
(*       workItem = workQueue.Dequeue(tl, ref missedSteal);                *)
(*       if (workItem == null)                                             *)
(*       {                                                                 *)
(*           if (missedSteal) workQueue.EnsureThreadRequested();           *)
(*           return true;                                                  *)
(*       }                                                                 *)
(*       // ... check quantum, continue loop ...                           *)
(*   }                                                                     *)
(*                                                                         *)
(* This model captures the loop by having workers transition to "looping"  *)
(* after processing, then back to checking for more work.                  *)
(***************************************************************************)

\* Worker finishes processing one item, will check for more
WorkerFinishProcessing(w) ==
    /\ workers[w] = "processing"
    /\ workers' = [workers EXCEPT ![w] = "looping"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested, enqueuingStopped>>

\* Worker in loop finds more work
WorkerLoopFindWork(w) ==
    /\ workers[w] = "looping"
    /\ queueSize > 0
    /\ queueSize' = queueSize - 1
    /\ workers' = [workers EXCEPT ![w] = "processing"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, enqueuers, workersRequested, enqueuingStopped>>

\* Worker in loop misses steal (lock contention)
WorkerLoopMissedSteal(w) ==
    /\ workers[w] = "looping"
    /\ queueSize > 0  \* Work exists but we couldn't get it
    \* Request another worker since we might have missed items
    /\ LET oldValue == hasOutstandingThreadRequest
       IN /\ hasOutstandingThreadRequest' = 1
          /\ IF oldValue = 0
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, enqueuers, enqueuingStopped>>

\* Worker in loop finds no more work - exits Dispatch
WorkerLoopNoWork(w) ==
    /\ workers[w] = "looping"
    /\ queueSize = 0
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested, enqueuingStopped>>

\* Worker in loop decides to yield (quantum expired or hill climbing)
\* Models lines 1071-1095 where worker returns without finding work
WorkerLoopYield(w) ==
    /\ workers[w] = "looping"
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested, enqueuingStopped>>

(***************************************************************************)
(* STOP ENQUEUEING - For closed system liveness checking                   *)
(*                                                                         *)
(* This action models the scenario where new work items stop being added.  *)
(* Once enqueuingStopped becomes TRUE, EnqueueStart is disabled.           *)
(* This allows us to verify that the queue eventually drains.              *)
(*                                                                         *)
(* NOTE: We don't require all enqueuers to be idle - in-progress enqueues  *)
(* will complete due to WF_vars(EnqueueEnsureRequested). This makes the    *)
(* action enabled more often, allowing SF_vars(StopEnqueueing) to fire.    *)
(***************************************************************************)
StopEnqueueing ==
    /\ ~enqueuingStopped
    /\ enqueuingStopped' = TRUE
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workers, workersRequested>>

(***************************************************************************)
(* STATE MACHINE                                                           *)
(***************************************************************************)

\* Core next-state relation (open system - enqueuers run forever)
NextOpen ==
    \/ \E e \in 1..NumEnqueuers:
        \/ EnqueueStart(e)
        \/ EnqueueEnsureRequested(e)
    \/ \E w \in 1..NumWorkers:
        \/ WorkerStart(w)
        \/ WorkerClearFlagAndCheck(w)
        \/ WorkerFindWork(w)
        \/ WorkerMissedSteal(w)
        \/ WorkerFindNoWork(w)
        \/ WorkerEnsureAnotherRequested(w)
        \/ WorkerFinishProcessing(w)
        \/ WorkerLoopFindWork(w)
        \/ WorkerLoopMissedSteal(w)
        \/ WorkerLoopNoWork(w)
        \/ WorkerLoopYield(w)

\* Extended next-state relation (closed system - enqueuers can stop)
Next == NextOpen \/ StopEnqueueing

(***************************************************************************)
(* FAIRNESS                                                                *)
(*                                                                         *)
(* Weak fairness ensures threads eventually make progress.                 *)
(*                                                                         *)
(* IMPORTANT: We do NOT add fairness to EnqueueStart because for liveness  *)
(* to hold (all work eventually processed), we must assume enqueuers       *)
(* eventually stop adding work. With WF on EnqueueStart, the model allows  *)
(* infinite enqueueing which prevents the queue from ever becoming empty.  *)
(*                                                                         *)
(* We also don't add fairness to:                                          *)
(*   - WorkerMissedSteal / WorkerLoopMissedSteal: exceptional conditions   *)
(*   - WorkerLoopYield: optional early exit                                *)
(*                                                                         *)
(* Strong fairness on WorkerFindWork/WorkerLoopFindWork ensures that if    *)
(* work is available infinitely often, workers will eventually get it      *)
(* (they won't miss steals forever).                                       *)
(***************************************************************************)
Fairness ==
    \* Enqueuers: only EnsureRequested has fairness (completes started enqueues)
    \* EnqueueStart does NOT have fairness - enqueuers may stop at any time
    /\ \A e \in 1..NumEnqueuers:
        WF_vars(EnqueueEnsureRequested(e))
    /\ \A w \in 1..NumWorkers:
        /\ WF_vars(WorkerStart(w))
        /\ WF_vars(WorkerClearFlagAndCheck(w))
        /\ WF_vars(WorkerFindWork(w))
        /\ WF_vars(WorkerFindNoWork(w))
        /\ WF_vars(WorkerEnsureAnotherRequested(w))
        /\ WF_vars(WorkerFinishProcessing(w))
        /\ WF_vars(WorkerLoopFindWork(w))
        /\ WF_vars(WorkerLoopNoWork(w))
        \* Strong fairness: if work is available infinitely often,
        \* workers will eventually get it (won't miss steals forever)
        /\ SF_vars(WorkerFindWork(w))
        /\ SF_vars(WorkerLoopFindWork(w))

\* Open system spec - enqueuers run forever, no termination possible
Spec == Init /\ [][NextOpen]_vars /\ Fairness

(***************************************************************************)
(* CLOSED SYSTEM FAIRNESS - For queue drain verification                   *)
(*                                                                         *)
(* ClosedFairness adds SF_vars(StopEnqueueing) which guarantees that       *)
(* enqueuers will eventually stop adding work. This allows us to verify    *)
(* that the queue eventually drains (AllWorkEventuallyProcessed).          *)
(*                                                                         *)
(* We need STRONG fairness (SF) here, not weak fairness (WF), because:     *)
(* - StopEnqueueing requires ~enqueuingStopped (can fire anytime)          *)
(* - But enqueuers may keep starting work, which we want to stop           *)
(* - SF guarantees action if infinitely often enabled                      *)
(***************************************************************************)
ClosedFairness ==
    /\ Fairness
    /\ SF_vars(StopEnqueueing)

\* Closed system spec - enqueuers eventually stop, system can terminate
ClosedSpec == Init /\ [][Next]_vars /\ ClosedFairness

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
(*                                                                         *)
(* This property captures the bug fixed in PR #121887. The key insight:    *)
(*   - Clearing flag BEFORE checking queue ensures no race window          *)
(*   - missedSteal handling ensures contention doesn't strand work         *)
(*   - Worker loop ensures continuous processing while work exists         *)
(***************************************************************************)
NoStrandedWork ==
    (queueSize > 0 /\ ~EnqueueInProgress) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ hasOutstandingThreadRequest = 1

(***************************************************************************)
(* LIVENESS PROPERTIES                                                     *)
(*                                                                         *)
(* Note on liveness with continuous enqueueing:                            *)
(* The property "all work eventually processed" cannot hold if enqueuers   *)
(* keep adding work faster than workers consume it. This is expected -     *)
(* real thread pools can have unbounded backlogs.                          *)
(*                                                                         *)
(* The SAFETY property NoStrandedWork guarantees that work is never        *)
(* forgotten - there's always a mechanism to process it. Liveness          *)
(* requires additional assumptions about the workload.                     *)
(***************************************************************************)

\* If no more work is enqueued, eventually all work gets processed
\* This requires removing WF_vars(EnqueueStart) from Fairness
AllWorkEventuallyProcessed == [](queueSize > 0 => <>(queueSize = 0))

\* Weaker property: if there's work and a pending request, eventually
\* a worker starts. This should always hold.
WorkerEventuallyStarts ==
    []((workersRequested > 0) => <>(\E w \in 1..NumWorkers: workers[w] # "idle"))

=============================================================================
