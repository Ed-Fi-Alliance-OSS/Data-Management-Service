# Story: Map FK Violations to Delete Conflict Responses

## Description

When a delete fails due to FK constraints, map the DB error to a DMS conflict response comparable to today’s `DeleteFailureReference`.

Preferred diagnostic source is `dms.ReferenceEdge`; deterministic FK naming is the fallback.

## Acceptance Criteria

- When a delete fails due to FK constraints, the response includes the set of referencing resource types (or sufficient diagnostics for clients).
- Mapping works for both PostgreSQL and SQL Server.
- If `dms.ReferenceEdge` is unavailable/broken, the system falls back to parsing the violated constraint name and returns a best-effort conflict response.

## Tasks

1. Implement dialect-specific FK violation detection and extraction of constraint name/details.
2. Implement mapping logic that returns conflict resources using `dms.ReferenceEdge` (preferred).
3. Implement fallback mapping from FK constraint name to resource type using deterministic naming rules.
4. Add tests covering both preferred and fallback mapping paths.

