# Editable stored-policy drift

`Claims.json` is an independent complete Filesystem input, not merge output. Its full
startup catalog is exactly one nonreserved set, `DriftEditable`. Its sole direct grant
is student Read with `NoFurtherAuthorizationRequired`. The student defaults also define
Create with that strategy, but a default alone grants nothing.

The literal metadata oracles cover the entire catalog before and after a secured PUT
of Read=true and Create=true to the existing claim-set resource-action endpoint. IDs
are resolved through the existing APIs; oracle authorization IDs only link records.
The only effective change is an added Create grant. `expected-verify-after.txt` is the
literal complete mismatch report; the initial report must be an exact match (exit 0),
and the final report must contain only that difference (exit 2).

Unrestricted management stays disabled and `/management/current-claims` returns 404
before and after the edit. Stored metadata snapshots, reports, and file hashes are
retained outside the mount. All source fixtures and mounted files remain byte-identical.
No clone, reserved-flag edit, bundled catalog, or fragment fixture is involved.
