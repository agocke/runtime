--------------------------- MODULE FixedModel ----------------------------
(***************************************************************************)
(* TLA+ model of the FIXED ThreadPool work queue thread request mechanism  *)
(* from dotnet/runtime AFTER PR #121887.                                   *)
(*                                                                         *)
(* The fix simplified to a 2-state boolean flag with critical ordering:    *)
(*   Worker: Clear flag → Memory barrier → Check queue                     *)
(*   Enqueuer: Add work → Exchange flag to 1 (request if was 0)            *)
(*                                                                         *)
(* RUN: tlc FixedModel.tla -config FixedModel.cfg                          *)
(* EXPECTED: Model checking completed. No error has been found.            *)
(***************************************************************************)

EXTENDS Integers, FiniteSets

CONSTANTS
    MaxQueueSize,
    NumEnqueuers,
    NumWorkers

VARIABLES
    queueSize,                   \* Number of items in the work queue
    hasOutstandingThreadRequest, \* The synchronization flag (0 or 1)
    enqueuers,                   \* State of each enqueuer
    workers,                     \* State of each worker
    workersRequested             \* Number of worker threads requested

vars == <<queueSize, hasOutstandingThreadRequest, enqueuers, workers, workersRequested>>

WorkerStates == {"idle", "starting", "checking", "found_work", "processing"}

TypeOK ==
    /\ queueSize \in 0..MaxQueueSize
    /\ hasOutstandingThreadRequest \in {0, 1}
    /\ enqueuers \in [1..NumEnqueuers -> {"idle", "enqueuing"}]
    /\ workers \in [1..NumWorkers -> WorkerStates]
    /\ workersRequested \in 0..NumWorkers

Init ==
    /\ queueSize = 0
    /\ hasOutstandingThreadRequest = 0
    /\ enqueuers = [e \in 1..NumEnqueuers |-> "idle"]
    /\ workers = [w \in 1..NumWorkers |-> "idle"]
    /\ workersRequested = 0

(***************************************************************************)
(* ENQUEUE OPERATIONS (Fixed)                                              *)
(*                                                                         *)
(* EnsureThreadRequested: Exchange flag to 1, request worker if was 0      *)
(***************************************************************************)
EnqueueStart(e) ==
    /\ enqueuers[e] = "idle"
    /\ queueSize < MaxQueueSize
    /\ queueSize' = queueSize + 1
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "enqueuing"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, workers, workersRequested>>

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
(* WORKER OPERATIONS (Fixed)                                               *)
(*                                                                         *)
(* KEY FIX: Clear flag BEFORE checking queue.                              *)
(* This ensures any concurrent enqueue will see flag=0 and request worker. *)
(***************************************************************************)
WorkerStart(w) ==
    /\ workers[w] = "idle"
    /\ workersRequested > 0
    /\ workersRequested' = workersRequested - 1
    /\ workers' = [workers EXCEPT ![w] = "starting"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers>>

\* THE FIX: Clear flag FIRST, then check queue
WorkerClearFlagAndCheck(w) ==
    /\ workers[w] = "starting"
    /\ hasOutstandingThreadRequest' = 0
    /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

WorkerFindWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize > 0
    /\ queueSize' = queueSize - 1
    /\ workers' = [workers EXCEPT ![w] = "found_work"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, enqueuers, workersRequested>>

\* No work found - safe to exit because any concurrent enqueue saw flag=0
WorkerFindNoWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize = 0
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested>>

\* Before processing, ensure another worker requested for remaining items
WorkerEnsureAnotherRequested(w) ==
    /\ workers[w] = "found_work"
    /\ LET oldValue == hasOutstandingThreadRequest
       IN /\ hasOutstandingThreadRequest' = 1
          /\ IF oldValue = 0
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ workers' = [workers EXCEPT ![w] = "processing"]
    /\ UNCHANGED <<queueSize, enqueuers>>

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
(* SAFETY PROPERTY (will PASS)                                             *)
(***************************************************************************)
ActiveWorkers == {w \in 1..NumWorkers: workers[w] # "idle"}
EnqueueInProgress == \E e \in 1..NumEnqueuers: enqueuers[e] # "idle"

NoStrandedWork ==
    (queueSize > 0 /\ ~EnqueueInProgress) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ hasOutstandingThreadRequest = 1

=============================================================================
