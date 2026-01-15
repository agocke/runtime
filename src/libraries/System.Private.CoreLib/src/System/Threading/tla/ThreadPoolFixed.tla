-------------------------- MODULE ThreadPoolFixed --------------------------
(***************************************************************************)
(* TLA+ model of the FIXED ThreadPool work queue thread request mechanism  *)
(* from dotnet/runtime after PR #121887.                                   *)
(*                                                                         *)
(* This model demonstrates the corrected protocol that prevents work items *)
(* from being stranded in the queue.                                       *)
(***************************************************************************)

EXTENDS Integers, Sequences, FiniteSets

CONSTANTS
    MaxQueueSize,     \* Maximum items that can be in the queue
    NumEnqueuers,     \* Number of enqueuer threads
    NumWorkers        \* Number of worker threads

VARIABLES
    queueSize,                  \* Number of items in the work queue
    hasOutstandingThreadRequest,\* Boolean flag: 0 or 1
    enqueuers,                  \* State of each enqueuer
    workers,                    \* State of each worker
    workersRequested            \* Number of worker threads actually requested

vars == <<queueSize, hasOutstandingThreadRequest, enqueuers, workers, workersRequested>>

(***************************************************************************)
(* Worker states:                                                          *)
(* - "idle": Not currently processing                                      *)
(* - "starting": Thread requested, about to start                          *)
(* - "clearing_flag": About to clear hasOutstandingThreadRequest           *)
(* - "checking": Checking the queue after clearing flag                    *)
(* - "found_work": Found work, will ensure another request and process     *)
(* - "requesting": About to ensure thread requested for remaining work     *)
(* - "processing": Processing a work item                                  *)
(***************************************************************************)
WorkerStates == {"idle", "starting", "clearing_flag", "checking",
                 "found_work", "requesting", "processing"}

(***************************************************************************)
(* Type invariant                                                          *)
(***************************************************************************)
TypeOK ==
    /\ queueSize \in 0..MaxQueueSize
    /\ hasOutstandingThreadRequest \in {0, 1}
    /\ enqueuers \in [1..NumEnqueuers -> {"idle", "enqueuing", "ensuring"}]
    /\ workers \in [1..NumWorkers -> WorkerStates]
    /\ workersRequested \in 0..NumWorkers

(***************************************************************************)
(* Initial state                                                           *)
(***************************************************************************)
Init ==
    /\ queueSize = 0
    /\ hasOutstandingThreadRequest = 0
    /\ enqueuers = [e \in 1..NumEnqueuers |-> "idle"]
    /\ workers = [w \in 1..NumWorkers |-> "idle"]
    /\ workersRequested = 0

(***************************************************************************)
(* Enqueue action - models the Enqueue method                              *)
(* An enqueuer adds work to the queue, then calls EnsureThreadRequested    *)
(***************************************************************************)
EnqueueStart(e) ==
    /\ enqueuers[e] = "idle"
    /\ queueSize < MaxQueueSize
    /\ queueSize' = queueSize + 1
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "enqueuing"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, workers, workersRequested>>

(***************************************************************************)
(* EnsureThreadRequested - Interlocked.Exchange(ref flag, 1)               *)
(* Only request a worker if the flag was 0                                 *)
(***************************************************************************)
EnqueueEnsureThreadRequested(e) ==
    /\ enqueuers[e] = "enqueuing"
    /\ LET oldValue == hasOutstandingThreadRequest
       IN /\ hasOutstandingThreadRequest' = 1
          /\ IF oldValue = 0
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "idle"]
    /\ UNCHANGED <<queueSize, workers>>

(***************************************************************************)
(* Worker starts - a worker thread begins execution                        *)
(***************************************************************************)
WorkerStart(w) ==
    /\ workers[w] = "idle"
    /\ workersRequested > 0
    /\ workersRequested' = workersRequested - 1
    /\ workers' = [workers EXCEPT ![w] = "starting"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers>>

(***************************************************************************)
(* Worker clears the flag BEFORE checking the queue                        *)
(* This is the KEY FIX: clear flag first, then check queue                 *)
(* Any enqueue that happens after this will see flag=0 and request         *)
(***************************************************************************)
WorkerClearFlag(w) ==
    /\ workers[w] = "starting"
    /\ hasOutstandingThreadRequest' = 0
    /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker checks the queue and finds work                                  *)
(***************************************************************************)
WorkerFindWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize > 0
    /\ queueSize' = queueSize - 1
    /\ workers' = [workers EXCEPT ![w] = "found_work"]
    /\ UNCHANGED <<hasOutstandingThreadRequest, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker checks the queue and finds no work                               *)
(* Goes back to idle - any concurrent enqueue will have requested a worker *)
(***************************************************************************)
WorkerFindNoWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize = 0  \* Actually empty (no race here for simplicity)
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker found work - now ensures another thread is requested             *)
(* This is critical: request another worker BEFORE processing              *)
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

(***************************************************************************)
(* Worker finishes processing                                              *)
(***************************************************************************)
WorkerFinishProcessing(w) ==
    /\ workers[w] = "processing"
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<queueSize, hasOutstandingThreadRequest, enqueuers, workersRequested>>

(***************************************************************************)
(* Next state relation                                                     *)
(***************************************************************************)
Next ==
    \/ \E e \in 1..NumEnqueuers:
        \/ EnqueueStart(e)
        \/ EnqueueEnsureThreadRequested(e)
    \/ \E w \in 1..NumWorkers:
        \/ WorkerStart(w)
        \/ WorkerClearFlag(w)
        \/ WorkerFindWork(w)
        \/ WorkerFindNoWork(w)
        \/ WorkerEnsureAnotherRequested(w)
        \/ WorkerFinishProcessing(w)

(***************************************************************************)
(* Fairness - ensure threads eventually make progress                      *)
(***************************************************************************)
Fairness ==
    /\ \A e \in 1..NumEnqueuers:
        /\ WF_vars(EnqueueStart(e))
        /\ WF_vars(EnqueueEnsureThreadRequested(e))
    /\ \A w \in 1..NumWorkers:
        /\ WF_vars(WorkerStart(w))
        /\ WF_vars(WorkerClearFlag(w))
        /\ WF_vars(WorkerFindWork(w))
        /\ WF_vars(WorkerFindNoWork(w))
        /\ WF_vars(WorkerEnsureAnotherRequested(w))
        /\ WF_vars(WorkerFinishProcessing(w))

Spec == Init /\ [][Next]_vars /\ Fairness

(***************************************************************************)
(* SAFETY PROPERTY: No stranded work                                       *)
(* If there's work in the queue AND no enqueuer is mid-operation, either:  *)
(* - A worker is active (not idle), OR                                     *)
(* - A worker has been requested, OR                                       *)
(* - The flag indicates a worker will check                                *)
(***************************************************************************)
ActiveWorkers == {w \in 1..NumWorkers: workers[w] # "idle"}
EnqueueInProgress == \E e \in 1..NumEnqueuers: enqueuers[e] # "idle"

NoStrandedWork ==
    (queueSize > 0 /\ ~EnqueueInProgress) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ hasOutstandingThreadRequest = 1

(***************************************************************************)
(* STRONGER INVARIANT: After enqueueing, work cannot be stranded           *)
(* This captures the key insight: clearing flag BEFORE checking means      *)
(* any concurrent enqueue will request a worker                            *)
(***************************************************************************)
EnqueuersNotMidOperation == \A e \in 1..NumEnqueuers: enqueuers[e] = "idle"

NoStrandedWorkStrong ==
    (queueSize > 0 /\ EnqueuersNotMidOperation) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0

(***************************************************************************)
(* LIVENESS PROPERTY: All work eventually gets processed                   *)
(* This property SHOULD PASS with the fixed implementation                 *)
(***************************************************************************)
AllWorkProcessed == [](queueSize > 0 => <>(queueSize = 0))

=============================================================================
