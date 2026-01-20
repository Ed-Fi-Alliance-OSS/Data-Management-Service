# Story: Map FK Violations to Delete Conflict Responses

## Description

When a delete fails due to FK constraints, map the DB error to a DMS conflict response comparable to today’s `DeleteFailureReference`.

Primary diagnostic source is deterministic FK naming (constraint name → referencing resource/table). Optional richer diagnostics can be produced from the compiled relational model (no runtime-maintained reverse-edge table).

## Acceptance Criteria

- When a delete fails due to FK constraints, the response includes the set of referencing resource types (or sufficient diagnostics for clients).
- Mapping works for both PostgreSQL and SQL Server.
- Mapping uses the violated constraint name and returns a best-effort conflict response when full diagnostics are not available.

## Tasks

1. Implement dialect-specific FK violation detection and extraction of constraint name/details.
2. Implement mapping from FK constraint name to referencing resource type using deterministic naming rules.
3. Optionally: implement “who references me?” diagnostics using compiled inbound-reference queries for richer responses.
4. Add tests covering mapping behavior across both dialects.
