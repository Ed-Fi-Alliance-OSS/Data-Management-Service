---
design: DMS-916
---

# Story: Bootstrap Schema Deployment Safety

## Description

Implement the authoritative pre-start schema provisioning and validation step used by developer bootstrap.
This slice separates schema provisioning from seed loading and assigns those responsibilities to
`provision-dms-schema.ps1` - the phase command defined in `command-boundaries.md` Section 3.5 that invokes the
shared SchemaTools/runtime-owned path over the staged schema workspace that Story 00 has already resolved
for the run. `provision-dms-schema.ps1` consumes that staged schema context and expected
`EffectiveSchemaHash` as inputs to the authoritative path, but it does not publish a second
schema-readiness decision table of its own.
Under the strong DMS-916 reading, the staged schema workspace also defines the exact physical schema
footprint expected for the run, so changing the selected extension set changes the target schema that must
be provisioned or validated.

## Acceptance Criteria

- Before any DMS host starts, `provision-dms-schema.ps1` consumes the selected `ApiSchema*.json` files
  and expected `EffectiveSchemaHash` already produced by the schema-selection slice for the run.
- Same-checkout reruns reuse the existing staged schema workspace only when the intended staged schema set is
  identical. If it differs, bootstrap fails fast with teardown guidance rather than mutating files that a
  running DMS host or already-provisioned database may still depend on.
- The schema files hashed in bootstrap are the same files later read by Docker-hosted DMS and by
  IDE-hosted DMS.
- Bootstrap derives the target databases from the DMS instances selected or created for the run.
- After target selection, `provision-dms-schema.ps1` always invokes the authoritative
  SchemaTools/runtime-owned provisioning and validation path against those targets before any DMS process
  is expected to serve requests.
- `provision-dms-schema.ps1` passes the staged schema paths, target connection details, and expected
  `EffectiveSchemaHash` into that shared path. `EffectiveSchemaHash` is a shared input, not bootstrap's
  final serviceability decision.
- Story 01 depends on SchemaTools through a narrow public integration contract only:
  - documented command shape or equivalent helper inputs,
  - exit code `0` for success and non-zero for failure,
  - pass-through diagnostics surfaced to the user.
- Story 01 does not require bootstrap to parse stdout/stderr text to infer specific rejection categories or
  recreate undocumented SchemaTools decision rules.
- The shared path owns the live-state inspection, any required provisioning work, and the final acceptance
  or rejection of each target for the selected schema set.
- Successful reruns remain idempotent: the authoritative path may no-op when the target is already correctly
  provisioned, or complete the required provisioning work before DMS startup.
- Bootstrap keeps `AppSettings__DeployDatabaseOnStartup=false` and does not route schema work through DMS
  startup side effects.
- Selecting a different extension combination changes the physical schema target for the run; bootstrap does
  not silently reuse a database provisioned for a different selected schema set.
- If the authoritative path rejects a target, bootstrap fails fast and surfaces its diagnostics rather than
  attempting bootstrap-owned repair, migration, or alternate readiness rules.
- No bootstrap state file, alternate schema-bypass branch, or bootstrap-only schema-readiness classifier is
  introduced for this contract.

## Tasks

1. Supply `provision-dms-schema.ps1` with target instance details from `configure-local-dms-instance.ps1`:
   within a single wrapper invocation, accept in-memory instance IDs forwarded by `bootstrap-local-dms.ps1`;
   in a manual phase flow, resolve target instances through explicit `-InstanceId <guid[]>` or
   `-SchoolYear <int[]>` selectors via CMS-backed lookup (auto-select when exactly one match, fail fast
   on zero or multiple without an explicit selector). Supply the staged schema context produced by
   Story 00 alongside those resolved target details.
2. Inside `provision-dms-schema.ps1`, consume the staged schema paths and expected `EffectiveSchemaHash`
   produced by Story 00 and pass them unchanged into the authoritative provisioning helper.
3. Implement `provision-dms-schema.ps1` as an unconditional invocation of the authoritative
   SchemaTools/runtime-owned provisioning and validation path over the staged schema set before DMS starts,
   per `command-boundaries.md` Section 3.5.
4. Keep the committed default `AppSettings__DeployDatabaseOnStartup=false` in place and surface
   authoritative provisioning diagnostics directly rather than routing schema work through DMS startup side
   effects or inventing bootstrap-owned readiness rules. Treat SchemaTools as a black-box authority for
   success/failure: rely on invocation shape, exit code, and pass-through diagnostics rather than parsing
   undocumented stdout/stderr details.
5. Respect the immutable staged-schema contract from Story 00: reuse identical staged schema content as-is,
   but fail rather than attempting bootstrap-owned mutation, repair, or replacement of a different staged
   schema selection.

## Out of Scope

- Reintroducing a persisted bootstrap control plane or bootstrap-owned schema-readiness classifier.
- Adding an alternate schema-bypass branch instead of using the authoritative pre-start provisioning path.
- Introducing a second schema-resolution or provisioning path for IDE-hosted versus Docker-hosted DMS runs.
- Attempting bootstrap-owned in-place repair or migration of a database rejected by the authoritative path,
  including pruning tables from a broader extension selection to satisfy a narrower one.
- Treating `EffectiveSchemaHash` as the only serviceability authority when broader runtime and SchemaTools
  validation remains required.

## Design References

- [`../bootstrap-design.md`](../bootstrap-design.md), Sections 3.2, 5, 11.3, 11.5, and 14.3
- [`../command-boundaries.md`](../command-boundaries.md), Section 3.5 (`provision-dms-schema.ps1` - normative ownership contract)

