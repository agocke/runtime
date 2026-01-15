------------------------- MODULE ThreadPoolRace ---------------------------
(***************************************************************************)
(* A minimal TLA+ model showing the EXACT race condition fixed in PR #121887*)
(*                                                                         *)
(* This model focuses on the precise interleaving that causes the bug:     *)
(* 1. Worker sets state to "Determining" and issues memory barrier         *)
(* 2. Enqueuer adds item and sets state to "Scheduled"                     *)
(* 3. Worker checks queue but misses the item (or queue appears empty)     *)
(* 4. Worker CAS from Determining->NotScheduled FAILS (state is Scheduled) *)
(* 5. But instead of rechecking, a subtle bug allows the loop to exit      *)
(*    with work still in the queue and no worker scheduled                 *)
(*                                                                         *)
(* The key insight: The 3-state protocol has a window where the enqueuer   *)
(* sees a state that makes it NOT request a worker, but the worker then    *)
(* exits without processing the work.                                      *)
(***************************************************************************)

EXTENDS Integers, FiniteSets

(***************************************************************************)
(* We model a minimal scenario: 1 enqueuer, 1 worker                       *)
(***************************************************************************)

\* The 3-state scheme
CONSTANTS NotScheduled, Determining, Scheduled

VARIABLES
    queue,          \* Set of work items (simplified to count)
    stage,          \* The queue processing stage
    workerState,    \* Worker thread state
    enqueuerState,  \* Enqueuer thread state
    workerRequested \* Has a worker been requested?

vars == <<queue, stage, workerState, enqueuerState, workerRequested>>

(***************************************************************************)
(* Worker states in the buggy implementation                               *)
(***************************************************************************)
\* idle - not doing anything
\* entered - entered Dispatch, about to set Determining
\* set_determining - set stage to Determining, about to check queue
\* checking - checking the queue
\* found_nothing - checked queue, found nothing, about to CAS
\* cas_failed - CAS failed, about to retry
\* exiting - about to exit (this is where the bug can manifest)
\* processing - processing work

WorkerStates == {"idle", "entered", "set_determining", "checking", 
                 "found_nothing", "cas_failed", "exiting", "processing"}

EnqueuerStates == {"idle", "adding", "exchanging", "done"}

TypeOK ==
    /\ queue \in 0..3
    /\ stage \in {NotScheduled, Determining, Scheduled}
    /\ workerState \in WorkerStates
    /\ enqueuerState \in EnqueuerStates
    /\ workerRequested \in BOOLEAN

Init ==
    /\ queue = 0
    /\ stage = NotScheduled
    /\ workerState = "idle"
    /\ enqueuerState = "idle"
    /\ workerRequested = FALSE

(***************************************************************************)
(* ENQUEUER ACTIONS                                                        *)
(***************************************************************************)

\* Enqueuer starts adding work
EnqueueStart ==
    /\ enqueuerState = "idle"
    /\ queue < 3
    /\ queue' = queue + 1
    /\ enqueuerState' = "adding"
    /\ UNCHANGED <<stage, workerState, workerRequested>>

\* Enqueuer does Interlocked.Exchange(ref stage, Scheduled)
\* Returns the OLD value - only request worker if old was NotScheduled
EnqueueExchange ==
    /\ enqueuerState = "adding"
    /\ LET oldStage == stage
       IN /\ stage' = Scheduled
          /\ IF oldStage = NotScheduled
             THEN workerRequested' = TRUE
             ELSE UNCHANGED workerRequested
    /\ enqueuerState' = "done"
    /\ UNCHANGED <<queue, workerState>>

\* Enqueuer completes
EnqueueComplete ==
    /\ enqueuerState = "done"
    /\ enqueuerState' = "idle"
    /\ UNCHANGED <<queue, stage, workerState, workerRequested>>

(***************************************************************************)
(* WORKER ACTIONS (buggy implementation)                                   *)
(***************************************************************************)

\* Worker thread starts (responds to request)
WorkerStart ==
    /\ workerState = "idle"
    /\ workerRequested
    /\ workerRequested' = FALSE
    /\ workerState' = "entered"
    /\ UNCHANGED <<queue, stage, enqueuerState>>

\* Worker enters dispatch, expects stage = Scheduled
WorkerSetDetermining ==
    /\ workerState = "entered"
    /\ stage = Scheduled
    /\ stage' = Determining
    \* Memory barrier here - the order matters!
    /\ workerState' = "set_determining"
    /\ UNCHANGED <<queue, enqueuerState, workerRequested>>

\* Worker checks the queue
WorkerCheckQueue ==
    /\ workerState = "set_determining"
    /\ workerState' = "checking"
    /\ UNCHANGED <<queue, stage, enqueuerState, workerRequested>>

\* Worker finds work in queue
WorkerFoundWork ==
    /\ workerState = "checking"
    /\ queue > 0
    /\ queue' = queue - 1
    /\ workerState' = "processing"
    \* In buggy impl, would do more state management here
    /\ UNCHANGED <<stage, enqueuerState, workerRequested>>

\* Worker finds no work (even if queue > 0 - models race/missed steal)
\* THE BUG: This can happen due to timing even when queue has items
WorkerFoundNothing ==
    /\ workerState = "checking"
    /\ workerState' = "found_nothing"
    /\ UNCHANGED <<queue, stage, enqueuerState, workerRequested>>

\* Worker tries to CAS: Determining -> NotScheduled
WorkerCAS ==
    /\ workerState = "found_nothing"
    /\ IF stage = Determining
       THEN \* CAS succeeds
            /\ stage' = NotScheduled
            /\ workerState' = "exiting"
       ELSE \* CAS fails (stage is Scheduled) - should retry
            /\ UNCHANGED stage
            /\ workerState' = "cas_failed"
    /\ UNCHANGED <<queue, enqueuerState, workerRequested>>

\* CAS failed, worker should retry
\* But in buggy implementation, there's complexity here that can go wrong
WorkerRetryAfterCASFail ==
    /\ workerState = "cas_failed"
    /\ workerState' = "set_determining"
    /\ stage' = Determining
    /\ UNCHANGED <<queue, enqueuerState, workerRequested>>

\* Worker exits - goes back to idle
\* THE CRITICAL BUG MANIFESTATION POINT
WorkerExit ==
    /\ workerState = "exiting"
    /\ workerState' = "idle"
    /\ UNCHANGED <<queue, stage, enqueuerState, workerRequested>>

\* Worker finishes processing
WorkerFinishProcessing ==
    /\ workerState = "processing"
    /\ stage' = NotScheduled  \* Simplified
    /\ workerState' = "idle"
    /\ UNCHANGED <<queue, enqueuerState, workerRequested>>

(***************************************************************************)
(* NEXT STATE                                                              *)
(***************************************************************************)

Next ==
    \/ EnqueueStart
    \/ EnqueueExchange
    \/ EnqueueComplete
    \/ WorkerStart
    \/ WorkerSetDetermining
    \/ WorkerCheckQueue
    \/ WorkerFoundWork
    \/ WorkerFoundNothing
    \/ WorkerCAS
    \/ WorkerRetryAfterCASFail
    \/ WorkerExit
    \/ WorkerFinishProcessing

(***************************************************************************)
(* INVARIANTS                                                              *)
(***************************************************************************)

\* The key safety property: work should not be stranded
\* Work is stranded if: queue > 0 AND no worker active AND no request pending
\* AND state is NotScheduled (no one will check)

WorkNotStranded ==
    ~(queue > 0 
      /\ workerState = "idle" 
      /\ enqueuerState = "idle"
      /\ ~workerRequested
      /\ stage = NotScheduled)

\* Alternative formulation
NoDeadlock ==
    (queue > 0 /\ workerState = "idle" /\ enqueuerState = "idle") =>
        (workerRequested \/ stage # NotScheduled)

Spec == Init /\ [][Next]_vars

=============================================================================
