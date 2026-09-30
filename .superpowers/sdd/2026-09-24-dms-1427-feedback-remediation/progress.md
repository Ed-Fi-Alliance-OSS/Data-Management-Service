# SDD ledger — plan: docs/superpowers/plans/2026-09-24-dms-1427-feedback-remediation.md

## Setup

- Dedicated ticket branch `DMS-1427` is the approved current workspace; no branch switch or external worktree is needed.
- Plan and approved spec were read. The plan is consistent with the spec: no historical migration, health redesign, form-credential expansion, schema bypass, or automatic workspace replacement.
- Ruling: use this ticket branch as the implementation workspace — the user explicitly requested work in the current repository and branch; creating another worktree would violate that scope and adds no safety here.

## Preflight conflict scan

| Scope | Produces / consumes | Finding | Ruling |
| --- | --- | --- | --- |
| Task 1 ↔ Task 2 | Separate bootstrap wrapper/startup files versus schema-preparation file; no shared interface | No conflict. | Proceed independently. |
| Task 1 ↔ Task 3 | Wrapper parameters/guidance versus frontend URL helpers; no shared interface | No conflict. | Proceed independently. |
| Task 1 ↔ Task 4 | Both modify `BootstrapEntryPointWorkflow.Tests.ps1`; Task 1 adds runtime forwarding tests, Task 4 adds documentation assertions | Shared test file requires sequential edits; Task 4 must preserve Task 1 fixtures and assertions. | Run Task 1 and its review before Task 4; Task 4 appends focused documentation coverage. |
| Task 2 ↔ Task 3 | PowerShell logging versus C# metadata | No conflict. | Proceed independently. |
| Task 2 ↔ Task 4 | Separate files and test concerns | No conflict. | Proceed independently. |
| Task 3 ↔ Task 4 | Frontend C# versus Markdown/Pester | No conflict. | Proceed independently. |
| Task 1 internal | Public `-Rebuild`, forwarding, suppression, Pester recording | The existing internal parameter is named `RebuildLocalImages`; the plan replaces it with the public `Rebuild` input and maps to `start-local-dms.ps1 -r`. | Implement exactly one public local wrapper switch; keep published wrapper out of scope. |
| Task 2 internal | Existing formatter tests and one call-site substitution | The stricter formatter remains correct for non-path values. | Change only the prepared-workspace output call. |
| Task 3 internal | Discovery route construction and metadata response mutation | Core API service has no request context; changing its interface would expand scope. | Share route-prefix construction at the frontend boundary and mutate response-local clones only. |
| Task 4 internal | Normal startup docs and existing HTTP example | The guide must not claim migration/reset behavior or form-body DMS credentials. | Document only wrapper startup, explicit rebuild, and existing Discovery→Basic flow. |

## Decisions

- Ruling: the approved PO direction means no user-facing historical upgrade/reset explanation; only `.plans/comments.md` contains that Jira note. Cost if wrong: documentation would need a follow-up removal, but no runtime contract would be affected.

## Task 1

- Implementer commits: `ca20cda6`, `170fc27c`.
- Verification: focused Pester `BootstrapEntryPointWorkflow.Tests.ps1` passed 122/122; pre-implementation run was blocked by HKCU permissions and an elevated attempt hung before the successful bounded run.
- Review: initial review found a Minor alias-coverage gap; implementer added explicit `-r` alias metadata coverage in `170fc27c`; re-review approved with no findings.
- Ruling: Task 1 is complete and approved; proceed to Task 2.

## Task 2

- Implementer commit: `424a11a2`.
- Verification: focused Pester `BootstrapSchemaDeploymentSafety.Tests.ps1` passed 356, failed 0, skipped 8; the eight skips are pre-existing environment-gated cases. The initial sandbox run was blocked by HKCU registry permissions; the elevated run completed successfully.
- Review: static review found only the missing test evidence; after the focused run and exact report update, the finding is closed. No code-quality or scope findings.
- Ruling: Task 2 is complete and approved; proceed to Task 3.

## Task 3

- Implementer commits: `6d5b5680`, `34eb91c3`.
- Verification: focused frontend tests passed 81/81 with 0 failures/skips after the route-context fix; CSharpier checked all four changed files and `git diff --check` passed.
- Review: initial review found an Important mapped-unqualified qualifier collision; follow-up added positional-alias-only behavior for mapped metadata requests plus four exact collision tests; re-review approved with no findings.
- Ruling: Task 3 is complete and approved; proceed to Task 4.

## Task 4

- Implementer commits: `f9cbfdad`, `a359b456`.
- Verification: focused bootstrap workflow Pester passed 125/125 with 0 failures/skips; `git diff --check` passed.
- Review: initial review found a Low documentation-test gap for the `-r` alias; implementer added the exact alias assertion in `a359b456`; re-review found no findings.
- Ruling: Task 4 is complete and approved; all planned tasks are implemented.

## Resume verification — 2026-09-25

- Repository state: branch `DMS-1427`; `HEAD` is `a359b456`; no tracked working-tree changes; `.superpowers/` remains untracked bookkeeping.
- Focused frontend verification: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --no-restore --filter "FullyQualifiedName~MetadataModuleTests|FullyQualifiedName~Given_real_ApiSchema_change_queries_metadata|FullyQualifiedName~Given_DiscoveryEndpointModule"` passed 83/83.
- Focused bootstrap verification: `Invoke-Pester ./eng/docker-compose/tests/BootstrapEntryPointWorkflow.Tests.ps1 -Output Detailed -PassThru` passed 125/125 with 0 failures, skips, or inconclusive tests when run with the required HKCU registry access.
- Sandbox note: the same Pester command cannot create its HKCU test registry key in the restricted sandbox; this is an environment limitation, not an assertion failure.
- Reconciliation: all four plan tasks are complete according to commits, task reports, reviews, and fresh verification. The approved plan checkboxes remain historical unchecked markers and were not rewritten.
