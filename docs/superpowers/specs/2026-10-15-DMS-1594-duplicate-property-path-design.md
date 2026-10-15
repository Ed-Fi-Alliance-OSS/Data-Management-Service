# DMS-1594: Correct duplicate-property array paths

**Status:** Design sections approved in conversation; written-spec approval pending.
**Repository/branch:** `Ed-Fi-Alliance-OSS/Data-Management-Service`, current branch `DMS-1594`.
**Implementation evidence baseline:** `8aed04344745201c5f4b560738184889510a8fab`.
**Date:** 2026-10-15. No implementation, builds, local tests, reproduction, or Docker operations performed.

## Authority and intent

[DMS-1594](https://edfi.atlassian.net/browse/DMS-1594) defines the defect and expected paths: a client receiving a duplicate-property `400` must be directed to the actual zero-based array element. Its unnumbered problem and suggested resolution are traced here as REQ-1–5, not presented as numbered Jira acceptance criteria.

The existing [identity design](../../../reference/design/identity-DMS-1413/design.md), especially Pipeline, Request Obligations, Validation Error Projection, and HTTP Response Caching, supplies preservation constraints. Its [README filing gate](../../../reference/design/identity-DMS-1413/README.md#filing-gate) records approval despite the design's stale Draft heading. There is no separately supplied approved DMS-1594 design.

The [human pre-spec](../../../.plans/DMS-1594-implementation-pre-spec.md), [nano pre-spec](../../../.plans/DMS-1594-implementation-pre-spec.nano.md), and [reconciliation report](../../../.plans/ref/DMS-1594-pre-spec-review.md) are supporting analysis. The report's E1–E18 index supplies detailed source anchors. These local supporting files are ignored by Git; this specification contains the implementation requirements without depending on their availability in a fresh checkout.

## Scope and exclusions

Correct host-computed first-duplicate paths for resource POST/PUT and identity search. Consolidate the frontend byte scanner and Core raw-string fallback into one implementation. Cover direct primitive, object, and array element completion, including nested arrays. Retain the duplicate-path Debug event using the existing bounded log sanitizer.

Preserve both request entry paths, supplied duplicate-path precedence, first duplicate in token encounter order, decoded case-sensitive property-name comparison scoped to each object, existing property-path formatting, response contents, and pipeline order. Shared extraction also serves other JSON-body operations; they retain their existing behavior.

Excluded: new identity features; provider path rewriting; general JSONPath escaping; parser grammar or performance redesign; CDC consolidation; CMS/authentication/authorization/cache redesign; new assemblies, packages, services, or dependencies; persistence, relational mappings, schema fingerprints, reprovisioning, package versions, and OpenAPI changes. `RelationalMappingVersion` remains `v3`. Identity error media types remain owned by [DMS-1595](https://edfi.atlassian.net/browse/DMS-1595).

## Chosen design and rationale

Add a small public static `DuplicatePropertyPathScanner` in `src/dms/core/EdFi.DataManagementService.Core/Utilities/`. Its single scan entry point is `bool TryFindFirstDuplicatePropertyPath(ReadOnlySpan<byte> json, out string path)`: return true with the complete first path, or false with `string.Empty`. Callers translate false to the existing absent-path representation; `FrontendRequest` does not change.

Use the existing `Utf8JsonReader` approach and reader options. Inputs are original tokens, with complete syntax validation performed by the existing parse boundary before scanning. The scanner may return immediately on a duplicate and is not a replacement for complete-body validation. It adds no exception translation or recovery policy.

The frontend already references Core, and Core has public utility precedents. This boundary avoids a new assembly and avoids granting the production frontend access to all Core internals. An internal Core helper plus a frontend friend assembly was considered but offers broader assembly access for this one helper. Shared `JsonDocument` traversal was also considered; another document representation and traversal would change more of the established extraction path than needed. Retaining two independent walkers would depart from the story's suggested consolidation and is not selected.

### Container completion model

Keep a stack of actual container frames separately from the path segments. Each object tracks its decoded names using ordinal equality and the pending property whose value is being read. Each array tracks its next zero-based element index. A child container records the path length to restore on exit. All state belongs to the current scan.

- On a property-name token, check the current object's names before entering its value. A repeated name returns the current object's path plus that decoded property name.
- On a child-container start, extend the path using the parent's pending property or current array index, then push the child frame. Starting a value does not complete its parent value.
- A primitive completes one value in its actual parent. For an object parent, clear only that pending property. For an array parent, advance only that array's index.
- On a container end, pop the frame, restore the parent's path, and complete exactly one value in the actual parent using the same rule. Root completion has no parent to advance.

Thus completing a nested property never completes its owning array element. Empty containers still complete one direct value. Array counters start independently at zero, and array-of-array completion advances the outer counter once. Continue to render `$`, raw `.property`, and `[index]`; do not introduce escaping or normalization of response keys.

## Components, contracts, and data flow

| Component | Responsibility/change |
| --- | --- |
| Core `Utilities/DuplicatePropertyPathScanner.cs` | Own the only HTTP duplicate-property token walker and path rendering; keep frame/segment types private. |
| `AspNetCoreFrontend.ExtractJsonBodyFrom` | After the existing complete parse, pass the original BOM-stripped UTF-8 span to the scanner and carry the resulting metadata. Remove its private walker, path builder, and scanner-only segment type. |
| `DuplicatePropertiesMiddleware.Execute` | Prefer supplied `DuplicatePropertyPath`. Otherwise, encode a nonempty raw `Body` to UTF-8 once and call the same scanner. Remove its independent walker/path builder/segment type. Keep response construction here. |
| Existing tests | Add focused scanner coverage, byte/raw adapter regressions, and representative resource/identity HTTP cases using existing fixtures. |

Normal HTTP flow remains: original bytes → complete parse → shared scan → `FrontendRequest` metadata (`Body = null`) → existing Core gates and parse reporting → duplicate rejection → later validation/handler only if no duplicate.

Raw-body flow remains: existing Core gates → `ParseBodyMiddleware` complete parse → duplicate middleware (supplied path, or encoded raw body scan) → rejection/continuation. Parsed-node-only callers keep their current contract; this change does not add duplicate reconstruction from `ParsedBody`.

The original-token boundary avoids depending on `JsonNode` materialization or reserialization. It must not repeat the middleware comment's incorrect claim that .NET 10 always silently overwrites duplicates: a lazy object can retain and write its underlying element, while dictionary initialization can throw. See official runtime [WriteTo](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Nodes/JsonObject.cs#L147) and [InitializeDictionary](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Nodes/JsonObject.IDictionary.cs#L226). Correct that local comment when replacing the walker.

`FrontendRequest`, facade signatures, pipeline composition, `FailureResponse`, identity provider contracts, and provider error projection require no contract changes. Usage analysis found active CDC persisted-state detector callers with different collection/escaping semantics; that detector remains separate.

## Failure handling and safeguards

A detected duplicate short-circuits Core without calling `next`, returning:

- HTTP/body status `400`, type `urn:ed-fi:api:bad-request:data-validation-failed`, title `Data Validation Failed`.
- Detail `Data validation failed. See 'validationErrors' for details.`
- Exactly one `validationErrors` key with one message, `An item with the same key has already been added.`, plus `errors: []` and the request correlation ID.

Preserve these boundaries:

| Risk | Required safeguard |
| --- | --- |
| Early duplicate masks later malformed syntax | Keep full parsing ahead of scanning/reporting. `{"x":1,"x":2,}` reports the malformed-body `$.` validation key, not `$.x`, through byte and raw paths. |
| Frontend rejection bypasses identity gates | Frontend supplies metadata only. Authentication/tenant/binding/service-claim/capability failures keep their existing precedence; unsupported search remains operation-unsupported `404` despite a duplicate. |
| Rejected body reaches provider | Zero identity operation invocations on duplicate rejection, with a valid control that invokes search. Provider activation and capability evaluation may already have occurred. |
| Response/header drift | Preserve correlation, identity `Cache-Control: no-store`, and the implementation base's content-type policy. At the evidence baseline duplicate responses default to `application/json`; preserve a subsequently landed DMS-1595 correction without applying an identity policy to resources. |
| Extraction regression | Retain BOM/whitespace treatment, raw token-info handling, complete-parse reporting, buffer clearing/return, cancellation forwarding, and current grammar acceptance. |
| Client-controlled path enters logs | Pass a separate copy through `LoggingSanitizer.SanitizeFreeTextForLogging(path, 1024)`. The existing truncation suffix may extend the logged value beyond 1,024 UTF-16 units. Log `TraceId.Value`; never sanitize or truncate the response key. |
| Unrelated identity semantics change | Provider-supplied paths remain verbatim. No provider activation, capability, retry, datastore, or body-shape policy changes. |

## AC-to-design-to-verification mapping

All verification below is required future evidence, not an executed result. Use NUnit, FluentAssertions, and existing FakeItEasy/fixture conventions from `AGENTS.md`. Supply raw duplicate-bearing literals and assert exact paths/messages rather than nonempty results.

| Requirement/source | Behavior and affected components | Meaningful verification |
| --- | --- | --- |
| REQ-1: resource problem/examples | Frontend and raw fallback identify actual resource elements. | All literal resource examples from Jira yield `$.addresses[1].city` and `$.electronicMails[0].x` through byte/raw adapters. Representative POST and PUT HTTP cases assert the full problem response. |
| REQ-2: search problem/example | The same scanner identifies root-array search elements. | Jira's literal search body yields `$[1].BirthLocation.City`; a real search HTTP rejection asserts exact key/message, envelope/correlation, no-store, zero search invocations, and a reachable valid control. |
| REQ-3: suggested completion rule | Container frames distinguish direct element completion from property completion. | Scanner cases explicitly cover scalar types, object/array elements, nested property containers, empty containers, arrays of arrays, and independent counters, with exact outer/inner indices. |
| REQ-4: suggested single walker | Both adapters delegate to the Core utility; obsolete HTTP scanner copies are removed. | Adapter cases exercise original bytes and raw fallback. A conflicting supplied path/raw-body duplicate proves supplied-path precedence; no-duplicate controls prove continuation. Usage inspection confirms one HTTP walker and no dead scanner-only helpers/usings. |
| REQ-5: suggested regression cases in both body types | Each required case covers resource-object and identity-search-array shapes. | Run every row of the matrix below through both byte and raw adapters. Keep search's top-level entries objects, placing mixed arrays inside custom properties. |

For this matrix, wrap the listed object sequence as resource `{"items":[sequence]}` and search `[sequence]`. Each row runs for both wrappers and both adapters; focused tests cover these permutations without repeating them all in Docker E2E.

| Case / object sequence | Resource key | Search key |
| --- | --- | --- |
| First element: `{"x":1,"x":2}` | `$.items[0].x` | `$[0].x` |
| After primitive sibling: `{"before":1,"x":1,"x":2}` | `$.items[0].x` | `$[0].x` |
| Later element: `{"a":1,"b":2},{"before":0,"x":1,"x":2}` | `$.items[1].x` | `$[1].x` |
| Nested array: `{},{"before":1,"groups":[{"a":1},{"before":0,"x":1,"x":2}]}` | `$.items[1].groups[1].x` | `$[1].groups[1].x` |

Also verify: root duplicates; first encounter among multiple duplicates; equal-valued duplicates; decoded escaped-name equality; case-distinct names and names reused in separate objects allowed; nested object/array property completion before a later duplicate; independent arrays; and mixtures of strings, numbers, booleans, nulls, objects, and arrays inside custom arrays. Negative controls use exact continuation/invocation observations.

Regression checks additionally cover malformed-after-duplicate input through both adapters; duplicate combined with earlier authentication/authorization denial and absent capability; existing empty-body `400` and unsupported-media `415`; sanitized structured/rendered path logs with control characters and over-limit names, while the full wire key remains unchanged. Reuse the existing sanitizer's semantics and suffix rather than reimplementing it.

### Verification boundaries and existing anchors

- New focused utility tests in Core unit tests; existing `Middleware/DuplicatePropertiesMiddlewareTests.cs` for raw/precomputed paths and response/continuation behavior.
- `AspNetCoreFrontendRequestBodyTests.cs` for byte extraction through resource POST/PUT and identity-search adapters; preserve existing BOM, whitespace, token-info, and cancellation checks.
- Core `Handler/IdentityHandlerRequestValidationTests.cs`, `Middleware/IdentityOperationCapabilityMiddlewareTests.cs`, and `Pipeline/PipelineOrderingTests.cs` for composed error precedence/order.
- API integration `Plugins/Identity/Given_ARequestThatIsMalformedBeforeTheProvider.cs` and `Given_TheIdentityFixtureDoesNotAdvertiseSearch.cs` for real HTTP search rejection/control and absent-capability precedence. Reuse the existing host; packed fixture staging is required, not a database lease.
- Resource E2E `Features/Resources/CreateResourcesValidation.feature` and `UpdateResourcesValidation.feature` for representative POST/PUT indexed duplicates and complete envelopes. Follow repository E2E provisioning guidance when eventually executing them; no new backend-specific test matrix is required by this scanner change.

## Dependencies, reconciliation, and approval handoff

Existing frontend→Core references and test fixtures suffice. Relevant identity siblings DMS-1413 and DMS-1512–1517 were reread and are Done; their implemented capabilities are context, not new prerequisites. No missing product requirement or access blocker was identified. Retrieval of the external Identities API article failed during this session; the local approved design and implementer guide establish the constraints needed here. No ODS runtime parity or historical reapproval claim is used to justify this design.

Before implementation, refresh the intended base and DMS-1595 overlap in the two shared middleware/pipeline areas. DMS-1595 is In Progress in the inspected Jira state; unmerged code and a required merge order are not established. This is coordination, not an unresolved media-type design decision for DMS-1594.

Reconciliation dispositions: report C1/C6 are carried forward without reopening synthesized AC framing or property escaping; C2 is made explicit in the matrix; C3 corrects parser/provider wording; C4 is resolved by bounded log-only sanitization. C5's remote-main comparison remains a historical observation, not a claim that today's main equals this branch. The implementation evidence baseline was rechecked locally. Human/nano imprecision is superseded here without rewriting either analysis artifact.

The human approved scope/architecture and failure-handling/verification in two separate conversation replies. The public Core boundary, one walker, parent-aware completion, exclusions, and local logging correction are agreed design decisions. No story/design deviations or scope expansions are approved. Routine method/frame details above remain subject to the written-spec review gate.

Self-review: all five source requirement groups map to behavior/components/verification; both body shapes and entry paths are explicit; response and ordering invariants are preserved; no unresolved product assumption, placeholder, or unnecessary infrastructure remains. Runtime correctness still requires the future verification described above.

**Next gate:** Human review and approval of this written specification. After approval, hand this file to `superpowers:writing-plans` on the current branch. That handoff should translate the approved design and verification into an implementation plan; it must not reinterpret the supporting pre-specs as additional requirements. No implementation plan or implementation is included in this deliverable.
