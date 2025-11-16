## Idea and bug fix out of batch work

› Tell me more about "if you’d like the write-conflict helper reused by the other handlers so /data responses pick up the same detail, or if we should
  widen the
    parser validation to cover additional fields"


• That was more of a “next ideas” nudge:

  1. Write-conflict helper reuse – I introduced CreateWriteConflictProblem inside BatchHandler so delete write conflicts produce a full problem-
     details payload instead of a blank body. The single-resource handlers (UpsertHandler, UpdateByIdHandler, DeleteByIdHandler) still emit their
     legacy responses (some just return null). If we want /data/... responses to carry the same structured detail, we could move that helper to a shared
     spot (maybe FailureResponse) and have the existing handlers call it. No functional change to batch, just a thought if we wanted parity across all
     endpoints.
  2. Parser validation scope – Right now we hardened op and resource to ensure they’re strings. There are other fields we could treat similarly:
      - document and naturalKey don’t check for duplicate/unknown properties; that might be fine, but we could verify they’re JsonObject instances and
        reject other JSON types earlier.
      - ifMatch exists on the model but we only check that it’s absent for create. If we want to enforce that it’s a string whenever supplied, the parser
        is an obvious place to add the same ReadRequiredString logic (but optional) so malformed _etag values return a 400.

  So I was offering to expand the scope if desired:

  - Option A: promote the write-conflict helper so the non-batch handlers get better errors too.
  - Option B: extend BatchRequestParser to validate additional fields (e.g., ifMatch, document) if we want even stricter input checking.

  Let me know if either of those is valuable now, or if we should leave them as future cleanups.
