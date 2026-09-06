# CDC Runbook Evidence Index

This index records evidence for the [CDC runbook](operations-runbook.md), not a second
behavioral specification. DMS-1326 owns exercised-runbook evidence for `CDC-INV-14` and
`CDC-INV-15`; see the [design traceability table](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability).
Other invariants retain their owning suites. Projection evidence remains in the
[E18 matrix](../document-cache-documentation/cdc-inv-evidence.md).

## Row format and evidence layers

Each row identifies a delivered runbook anchor, design owner, applicable invariant IDs,
exact test identity (including parameterized cases), provider and evidence layer, actual
result, sanitized artifact location, and pending verification owner if any. A manual
review uses its review ID instead of inventing a test identity. An artifact accompanying
a row records revision, tools/provider/image versions, starting directory, invocation,
stdout/stderr and exit code, sanitization, and cleanup disposition as applicable.

Use separate rows for manual review, mocked CLI contracts, provider integration,
broker/connector integration, and API-driven exercises. A passing mock is not provider
evidence; an unavailable prerequisite or skipped case is not a pass. Keep output captured
before sanitization in the controlled local evidence workspace; publish only reviewed
sanitized artifacts while preserving relevant JSON fields and outcome/exit-code pairs.
No executable documentation catalog or automated documentation/link tests are used.

| Runbook anchor / review | Design owner / invariants | Exact identity and layer | Result / artifact | Remaining owner |
| --- | --- | --- | --- | --- |
| [Prerequisites](operations-runbook.md#prerequisites), [state](operations-runbook.md#deployment-state), [format](operations-runbook.md#procedure-format); T01 foundation | [Configuration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection), [binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 authoring support only | Manual review `T01-foundation`; no provider access | Reviewed; [record below](#t01-foundation-review), [root help](evidence/t01/help.txt), [CDC help](evidence/t01/cdc-help.txt) | Operational replay pending T14/T15; final reconciliation T16 |
| [Prerequisites/configuration](operations-runbook.md#prerequisites); T01 validation support | [Configuration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection); CDC-INV-15 support, not exercised-runbook closure | `EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit.Given_CdcControlOptionsTests`; all 77 exact selected case identities in artifact; provider-independent unit behavior | 77 passed, 0 failed, 0 skipped; [case results](evidence/t01/options-test-results.txt) | Provider claims still pending T14/T15 |

<a id="t01-foundation-review"></a>
## T01 foundation review

Reviewed 2026-09-06 against source revision
`b1f57aae6aece5e77084f687aa148100f1035c7b` plus T01 documentation changes. The pre-existing
story edit was used as input and left outside this task's commit. Tool: .NET SDK
`10.0.102`, Linux/Bash. No provider, broker, or qualified image was started or exercised.
No deployment resources required cleanup. Raw stdout/stderr and TRX were captured before
sanitization under `/tmp/dms-1326-t01`; that temporary workspace is not durable evidence.
Reviewed help retains all text with its extra terminal blank line removed for repository
whitespace checks; test-case extraction omits host/user paths and run IDs.
The committed artifacts above retain the evidence needed for this task.

**Starting directory:** repository root. **Prerequisites:** repository .NET SDK and
restore access. **Target/generation:** none; help and isolated option tests do not invoke
deployment operations. **Effect:** local build/help/test only.

Commands executed:

```bash
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- --help
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc --help
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter FullyQualifiedName~CdcControlOptions --logger 'trx;LogFileName=cdc-control-options.trx' --results-directory /tmp/dms-1326-t01
```

**Outcome:** both help commands exited `0`, emitted text, and had empty stderr; no JSON
contract applies. The test command exited `0`, with 77 passed and no failures/skips.
**Verification:** manually compared command groups/options with the CLI reference and
`DocumentCacheAdminCommandSurface`; compared configuration with `CdcControlOptions`, its
validator, `DocumentCacheAdminConfiguration`, the command surface overrides, dispatcher,
provider-input factory, renderer/shared rules/input validator, lag reader, state store,
and Compose enable/resolver/mount code. Manually followed the new local links/anchors
and checked procedure conventions against the story's Runnable Examples and Setup Handoffs.
**Interruption/retry:** these commands mutate no deployment state and may be rerun from
the same directory. They do not validate operator credentials or live readiness.

Material findings and corrections:

- CLI configuration directly loads JSON and environment providers, not user secrets or
  remote secret providers. Corrected that claim in the CLI README and catalog; documented
  the actual precedence and the distinction between request-only and override flags.
- Separate Java connector and librdkafka admin credentials; database principals are
  required on every verb, while typed Kafka principals are ACL-conditional. Consumer
  principal/group uniqueness applies even with ACLs disabled.
- SQL Server poll interval has no renderer default and must be no greater than the
  heartbeat interval; generic option validation alone is insufficient. Recorded the
  default heartbeat, producer buffer minimum, timeout units, and metrics-URI derivation.
- Wrapper mount precedence differs from direct CLI configuration. Documented the host
  versus `/state` handoff and the default root's dependence on the starting directory.
- Marked status containment effects, external write-admission authority, single-controller
  state storage, and the boundary between initial admission and eventual observation.
- Operational JSON examples are not introduced by T01. T03 captures existing status
  fixtures before sanitizing; missing assertions and live replays remain pending below.
  The E18 runbook's packaged-history explanation and old discovery links are reconciled
  by T04/T09, not treated as verified CDC recovery instructions here.

<a id="pending-delivery"></a>
## Pending delivery and verification

These are explicit pending results, not runnable anchors or claims of coverage. Each
authoring task adds its actual procedure anchors and evidence rows when delivered.
Authoring can finish against existing help/source/available fixture output; new assertions
and live replays stay assigned to the downstream owners. T16 closes these entries using
actual results before the story is complete.

| Pending scope | Authoring / evidence owner | Result and required handoff |
| --- | --- | --- |
| Fresh PostgreSQL/SQL Server setup, API upsert/delete, planned stop/guarded restart | T02; T14/T15 live replay | Pending. Record qualified image, exact wrapper arguments, persistent mount, and E19-06 harness identities; an absent upstream harness is an unmet dependency. |
| Monitoring/incident routing and status/lag JSON | T03; T14/T15 live observations | Pending. Capture existing serialized fixtures before sanitization; distinguish `notReady`/`unknown` success exits and containment effects. |
| E18 packaged downstream-history handoff and repair scope | T04; T12 additional assertions | Pending. Link E18 evidence; require exact shipped-composition cases per provider and rejected evidence shape. |
| Continuity, complete-record adoption, physical-source replacement | T05; T13 assertions; T14/T15 replay | Pending. Record exact case identities, default-tenant translation, outcome/exit code, preserved generation, and retry evidence. |
| Destructive retirement, partial failure, timeout and same-operation retry | T06; T17 assertions; T14/T15 replay | Pending. Distinguish successful cleanup proof from incomplete/refused cleanup; preserve shared state and retirement records. |
| Security, consumer isolation, sensitive-data containment | T07; T14/T15 replay | Pending. Link authorizer-backed evidence separately from local ACL-disabled results; component cleanup cannot prove platform purge. |
| Provider retention, consumer continuity, record budget, capacity observations | T08; T14/T15 replay | Pending. Link existing owning evidence; small exercises do not establish production capacity. |
| Discovery references and final consistency | T09; T16 closure | Pending. Final manual review reconciles all anchors, exact test selections, provider results, artifacts, and limitations. |

Setup, provider artifacts, routing, durability, ACLs, and consumer conformance remain in
their sibling stories/suites. Start with the
[CDC contract-to-evidence ownership table](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability)
and the [E18 evidence matrix](../document-cache-documentation/cdc-inv-evidence.md);
later rows link exact reusable test identities rather than copying those implementations.
