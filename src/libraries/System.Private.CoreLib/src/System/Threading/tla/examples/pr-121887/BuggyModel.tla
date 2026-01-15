--------------------------- MODULE BuggyModel ----------------------------
(***************************************************************************)
(* TLA+ model of the BUGGY ThreadPool work queue thread request mechanism  *)
(* from dotnet/runtime BEFORE PR #121887.                                  *)
(*                                                                         *)
(* This model demonstrates the race condition where work items can become  *)
(* stranded in the queue with no workers scheduled to process them.        *)
(*                                                                         *)
(* The original implementation used a 3-state scheme:                      *)
(*   NotScheduled -> Scheduled (on enqueue)                                *)
(*   Scheduled -> Determining (worker starts checking)                     *)
(*   Determining -> NotScheduled (if no work) or Scheduled (if work found) *)
(*                                                                         *)
(* RUN: tlc BuggyModel.tla -config BuggyModel.cfg                          *)
(* EXPECTED: Invariant NoStrandedWork is VIOLATED                          *)
(***************************************************************************)

EXTENDS Integers, FiniteSets

CONSTANTS
    MaxQueueSize,
    NumEnqueuers,
    NumWorkers

(***************************************************************************)
(* The 3-state scheme from the buggy implementation                        *)
(***************************************************************************)
QueueProcessingStages == {"NotScheduled", "Determining", "Scheduled"}

VARIABLES
    queueSize,              \* Number of items in the work queue
    queueProcessingStage,   \* Current state of the scheduling mechanism
    enqueuers,              \* State of each enqueuer
    workers,                \* State of each worker
    workersRequested        \* Number of worker threads requested

vars == <<queueSize, queueProcessingStage, enqueuers, workers, workersRequested>>

WorkerStates == {"idle", "starting", "checking", "found_work",
                 "found_nothing", "processing"}

TypeOK ==
    /\ queueSize \in 0..MaxQueueSize
    /\ queueProcessingStage \in QueueProcessingStages
    /\ enqueuers \in [1..NumEnqueuers -> {"idle", "enqueuing"}]
    /\ workers \in [1..NumWorkers -> WorkerStates]
    /\ workersRequested \in 0..NumWorkers

Init ==
    /\ queueSize = 0
    /\ queueProcessingStage = "NotScheduled"
    /\ enqueuers = [e \in 1..NumEnqueuers |-> "idle"]
    /\ workers = [w \in 1..NumWorkers |-> "idle"]
    /\ workersRequested = 0

(***************************************************************************)
(* ENQUEUE OPERATIONS (Buggy)                                              *)
(*                                                                         *)
(* Enqueuer adds work, then does Exchange(ref stage, Scheduled).           *)
(* Only requests a worker if old state was NotScheduled.                   *)
(* BUG: Does NOT request if state was Determining or Scheduled!            *)
(***************************************************************************)
EnqueueStart(e) ==
    /\ enqueuers[e] = "idle"
    /\ queueSize < MaxQueueSize
    /\ queueSize' = queueSize + 1
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "enqueuing"]
    /\ UNCHANGED <<queueProcessingStage, workers, workersRequested>>

EnqueueFinish(e) ==
    /\ enqueuers[e] = "enqueuing"
    /\ LET oldStage == queueProcessingStage
       IN /\ queueProcessingStage' = "Scheduled"
          \* BUG: Only request worker if NotScheduled
          \* If Determining, we assume worker will see our work (but they might not!)
          /\ IF oldStage = "NotScheduled"
             THEN workersRequested' = workersRequested + 1
             ELSE UNCHANGED workersRequested
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "idle"]
    /\ UNCHANGED <<queueSize, workers>>

(***************************************************************************)
(* WORKER OPERATIONS (Buggy)                                               *)
(*                                                                         *)
(* Worker sets state to Determining BEFORE checking queue.                 *)
(* This creates a window where enqueuer sees Determining and doesn't       *)
(* request a worker, but worker then exits without finding the work.       *)
(***************************************************************************)
WorkerStart(w) ==
    /\ workers[w] = "idle"
    /\ workersRequested > 0
    /\ workersRequested' = workersRequested - 1
    /\ workers' = [workers EXCEPT ![w] = "starting"]
    /\ UNCHANGED <<queueSize, queueProcessingStage, enqueuers>>

\* BUG: Set state to Determining BEFORE checking the queue
\* This is wrong because there's a window where enqueue sees Determining
\* but worker hasn't checked the queue yet
WorkerSetDetermining(w) ==
    /\ workers[w] = "starting"
    /\ queueProcessingStage = "Scheduled"
    /\ queueProcessingStage' = "Determining"
    /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

\* Worker finds work
WorkerFindWork(w) ==
    /\ workers[w] = "checking"
    /\ queueSize > 0
    /\ queueSize' = queueSize - 1
    /\ workers' = [workers EXCEPT ![w] = "found_work"]
    /\ UNCHANGED <<queueProcessingStage, enqueuers, workersRequested>>

\* Worker "finds" no work - can happen due to timing even if queue not empty
\* This models the "missed steal" scenario or timing issues
WorkerFindNoWork(w) ==
    /\ workers[w] = "checking"
    /\ workers' = [workers EXCEPT ![w] = "found_nothing"]
    /\ UNCHANGED <<queueSize, queueProcessingStage, enqueuers, workersRequested>>

\* BUG: Worker CAS from Determining to NotScheduled and exits
\* If an enqueuer just added work and saw Determining, no worker is coming!
WorkerCASAndExit(w) ==
    /\ workers[w] = "found_nothing"
    /\ IF queueProcessingStage = "Determining"
       THEN \* CAS succeeds - go to NotScheduled and exit
            /\ queueProcessingStage' = "NotScheduled"
            /\ workers' = [workers EXCEPT ![w] = "idle"]
       ELSE \* CAS fails (state is Scheduled) - try again
            /\ UNCHANGED queueProcessingStage
            /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

WorkerStartProcessing(w) ==
    /\ workers[w] = "found_work"
    /\ workers' = [workers EXCEPT ![w] = "processing"]
    /\ UNCHANGED <<queueSize, queueProcessingStage, enqueuers, workersRequested>>

WorkerFinishProcessing(w) ==
    /\ workers[w] = "processing"
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    /\ queueProcessingStage' = "NotScheduled"
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

(***************************************************************************)
(* STATE MACHINE                                                           *)
(***************************************************************************)
Next ==
    \/ \E e \in 1..NumEnqueuers:
        \/ EnqueueStart(e)
        \/ EnqueueFinish(e)
    \/ \E w \in 1..NumWorkers:
        \/ WorkerStart(w)
        \/ WorkerSetDetermining(w)
        \/ WorkerFindWork(w)
        \/ WorkerFindNoWork(w)
        \/ WorkerCASAndExit(w)
        \/ WorkerStartProcessing(w)
        \/ WorkerFinishProcessing(w)

(***************************************************************************)
(* SAFETY PROPERTY (will be VIOLATED)                                      *)
(*                                                                         *)
(* Work should not be stranded: if there's work in queue and no enqueue    *)
(* in progress, either workers are active, workers are requested, or       *)
(* the state indicates someone will check.                                 *)
(***************************************************************************)
ActiveWorkers == {w \in 1..NumWorkers: workers[w] # "idle"}
EnqueueInProgress == \E e \in 1..NumEnqueuers: enqueuers[e] # "idle"

NoStrandedWork ==
    (queueSize > 0 /\ ~EnqueueInProgress) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ queueProcessingStage \in {"Scheduled", "Determining"}

=============================================================================
