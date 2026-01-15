-------------------------- MODULE ThreadPoolBuggy --------------------------
(***************************************************************************)
(* TLA+ model of the BUGGY ThreadPool work queue thread request mechanism  *)
(* from dotnet/runtime before PR #121887.                                  *)
(*                                                                         *)
(* This model demonstrates the race condition where work items can become  *)
(* stranded in the queue with no workers scheduled to process them.        *)
(***************************************************************************)

EXTENDS Integers, Sequences, FiniteSets

CONSTANTS
    MaxQueueSize,     \* Maximum items that can be in the queue
    NumEnqueuers,     \* Number of enqueuer threads
    NumWorkers        \* Number of worker threads

(***************************************************************************)
(* The three-state scheme from the buggy implementation:                   *)
(* - NotScheduled: No worker is scheduled to check the queue               *)
(* - Determining: Worker is about to check the queue                       *)
(* - Scheduled: A worker is scheduled or will handle parallelization       *)
(***************************************************************************)
QueueProcessingStages == {"NotScheduled", "Determining", "Scheduled"}

VARIABLES
    queueSize,              \* Number of items in the work queue
    queueProcessingStage,   \* Current state of the scheduling mechanism
    enqueuers,              \* State of each enqueuer: "idle" or "enqueuing"
    workers,                \* State of each worker
    workersRequested        \* Number of worker threads requested (for tracking)

vars == <<queueSize, queueProcessingStage, enqueuers, workers, workersRequested>>

(***************************************************************************)
(* Worker states:                                                          *)
(* - "idle": Not currently processing                                      *)
(* - "starting": Thread requested, about to start                          *)
(* - "setting_determining": About to set state to Determining              *)
(* - "checking": Checking the queue after setting Determining              *)
(* - "found_work": Found work, will process it                             *)
(* - "no_work_cas": No work found, about to CAS to NotScheduled            *)
(* - "processing": Processing a work item                                  *)
(***************************************************************************)
WorkerStates == {"idle", "starting", "setting_determining", "checking", 
                 "found_work", "no_work_cas", "processing"}

(***************************************************************************)
(* Type invariant                                                          *)
(***************************************************************************)
TypeOK ==
    /\ queueSize \in 0..MaxQueueSize
    /\ queueProcessingStage \in QueueProcessingStages
    /\ enqueuers \in [1..NumEnqueuers -> {"idle", "enqueuing"}]
    /\ workers \in [1..NumWorkers -> WorkerStates]
    /\ workersRequested \in 0..NumWorkers

(***************************************************************************)
(* Initial state                                                           *)
(***************************************************************************)
Init ==
    /\ queueSize = 0
    /\ queueProcessingStage = "NotScheduled"
    /\ enqueuers = [e \in 1..NumEnqueuers |-> "idle"]
    /\ workers = [w \in 1..NumWorkers |-> "idle"]
    /\ workersRequested = 0

(***************************************************************************)
(* Enqueue action - models the Enqueue method                              *)
(* An enqueuer adds work to the queue, then uses Interlocked.Exchange      *)
(* to set state to Scheduled. Only requests a worker if previous state     *)
(* was NotScheduled.                                                       *)
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
          /\ IF oldStage = "NotScheduled"
             THEN \* Request a worker thread
                  /\ workersRequested' = workersRequested + 1
             ELSE \* Don't request - either Determining or Scheduled
                  /\ UNCHANGED workersRequested
    /\ enqueuers' = [enqueuers EXCEPT ![e] = "idle"]
    /\ UNCHANGED <<queueSize, workers>>

(***************************************************************************)
(* Worker starts - models a worker thread beginning execution              *)
(***************************************************************************)
WorkerStart(w) ==
    /\ workers[w] = "idle"
    /\ workersRequested > 0
    /\ workersRequested' = workersRequested - 1
    /\ workers' = [workers EXCEPT ![w] = "starting"]
    /\ UNCHANGED <<queueSize, queueProcessingStage, enqueuers>>

(***************************************************************************)
(* Worker sets state to Determining before checking the queue              *)
(* This is the critical step where the bug manifests - there's a window    *)
(* between setting Determining and actually checking the queue             *)
(***************************************************************************)
WorkerSetDetermining(w) ==
    /\ workers[w] = "starting"
    /\ queueProcessingStage = "Scheduled"
    /\ queueProcessingStage' = "Determining"
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
    /\ UNCHANGED <<queueProcessingStage, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker checks the queue and finds no work                               *)
(* Note: This can happen even if queueSize > 0 due to race conditions      *)
(* (modeling the "missed steal" scenario or timing issues)                 *)
(***************************************************************************)
WorkerFindNoWork(w) ==
    /\ workers[w] = "checking"
    /\ workers' = [workers EXCEPT ![w] = "no_work_cas"]
    /\ UNCHANGED <<queueSize, queueProcessingStage, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker found no work, tries to CAS from Determining to NotScheduled     *)
(* THE BUG: If state is still Determining, we set to NotScheduled.         *)
(* But an enqueuer may have just added work and set state to Scheduled,    *)
(* relying on us to process it (since we were in Determining).             *)
(***************************************************************************)
WorkerCASNoWork(w) ==
    /\ workers[w] = "no_work_cas"
    /\ IF queueProcessingStage = "Determining"
       THEN \* CAS succeeds - set to NotScheduled and stop
            /\ queueProcessingStage' = "NotScheduled"
            /\ workers' = [workers EXCEPT ![w] = "idle"]
       ELSE \* CAS fails - state is Scheduled, need to check again
            /\ UNCHANGED queueProcessingStage
            /\ workers' = [workers EXCEPT ![w] = "checking"]
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker found work, sets state appropriately and starts processing       *)
(***************************************************************************)
WorkerStartProcessing(w) ==
    /\ workers[w] = "found_work"
    \* In the buggy implementation, the worker would set state back to
    \* Determining, check for more work, and request another thread if needed
    \* For simplicity, we model this as just going to processing state
    /\ workers' = [workers EXCEPT ![w] = "processing"]
    /\ UNCHANGED <<queueSize, queueProcessingStage, enqueuers, workersRequested>>

(***************************************************************************)
(* Worker finishes processing and becomes idle                             *)
(***************************************************************************)
WorkerFinishProcessing(w) ==
    /\ workers[w] = "processing"
    /\ workers' = [workers EXCEPT ![w] = "idle"]
    \* After processing, in buggy impl it would set to NotScheduled
    \* if no more work was found
    /\ queueProcessingStage' = "NotScheduled"
    /\ UNCHANGED <<queueSize, enqueuers, workersRequested>>

(***************************************************************************)
(* Next state relation                                                     *)
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
        \/ WorkerCASNoWork(w)
        \/ WorkerStartProcessing(w)
        \/ WorkerFinishProcessing(w)

(***************************************************************************)
(* Fairness - ensure threads eventually make progress                      *)
(***************************************************************************)
Fairness ==
    /\ \A e \in 1..NumEnqueuers:
        /\ WF_vars(EnqueueStart(e))
        /\ WF_vars(EnqueueFinish(e))
    /\ \A w \in 1..NumWorkers:
        /\ WF_vars(WorkerStart(w))
        /\ WF_vars(WorkerSetDetermining(w))
        /\ WF_vars(WorkerFindWork(w))
        /\ WF_vars(WorkerCASNoWork(w))
        /\ WF_vars(WorkerStartProcessing(w))
        /\ WF_vars(WorkerFinishProcessing(w))

Spec == Init /\ [][Next]_vars /\ Fairness

(***************************************************************************)
(* SAFETY PROPERTY: No stranded work                                       *)
(* If there's work in the queue, either:                                   *)
(* - A worker is active (not idle), OR                                     *)
(* - A worker has been requested (workersRequested > 0), OR                *)
(* - The state indicates a worker will check (Scheduled or Determining)    *)
(***************************************************************************)
ActiveWorkers == {w \in 1..NumWorkers: workers[w] # "idle"}

NoStrandedWork ==
    (queueSize > 0) =>
        \/ Cardinality(ActiveWorkers) > 0
        \/ workersRequested > 0
        \/ queueProcessingStage \in {"Scheduled", "Determining"}

(***************************************************************************)
(* LIVENESS PROPERTY: All work eventually gets processed                   *)
(* This property will be VIOLATED by the buggy implementation              *)
(***************************************************************************)
AllWorkProcessed == <>(queueSize = 0)

=============================================================================
