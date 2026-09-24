# DMS-1427: Feedback Remediation Design

## Status and authority

This is the approved implementation-ready design for DMS-1427. The story
(`.plans/ref/DMS-1427.md`) and the EDFI-2881 feedback
(`.plans/ref/EDFI-2881.md`) are authoritative. The human and nano pre-specs,
and their review report, are supporting analysis only.

The branch inspected is `DMS-1427` at `aeb211a3fab6e1719e71a24a9a5df3e4e690c8d4`.
No tests were run while developing this specification.

## Scope

Correct the EDFI-2881 observations that remain applicable on this branch:

1. Make local-image rebuilding explicit on the normal local bootstrap entry
   point and clarify the normal local bootstrap path.
2. Advertise the public DMS OAuth proxy, rather than the container-internal
   upstream URL, in every served OpenAPI document.
3. Correct Windows-safe prepared-workspace logging.
4. Prevent wrapper-managed normal startup from printing terminal InfraOnly
   guidance before it starts DMS.
5. Clarify normal bootstrap and DMS token-proxy Basic authentication in the
   user-facing getting-started material.

## Exclusions and approved decisions

- Do not add database migration, compatibility, startup-detector, or general
  historical-upgrade work.
- Do not weaken effective-schema-hash checks or the staged-workspace mismatch
  stop. Guarded automatic replacement remains DMS-1271 scope.
- Do not change `AppSettings.AuthenticationService`: it remains the internal
  DMS-to-identity-provider forwarding target.
- Do not redesign CMS `/health`, add proxy form-body client credentials, or
  otherwise change those public contracts.
- Do not add automatic image rebuilding, a second build mechanism, or broad
  documentation reorganization.

## Chosen design

### Local bootstrap and documentation

Add `-Rebuild` with `-r` alias to `bootstrap-local-dms.ps1` and its shared
wrapper path. When supplied, the wrapper forwards the existing rebuild switch
only to its initial `start-local-dms.ps1` infrastructure invocation, which
already executes `docker compose build --no-cache`. The default remains
non-rebuild. `bootstrap-published-dms.ps1` is out of scope because the reported
failure is local-image reuse.

Update `GETTING_STARTED.md` to make `bootstrap-local-dms.ps1` the normal local
startup command. Preserve phase commands as advanced/manual guidance. Document
the explicit `-Rebuild` option as the way to rebuild local images when needed,
without adding separate historical-upgrade guidance.

The guide will state that API clients obtain the DMS proxy URL from Discovery
and submit `grant_type=client_credentials` with HTTP Basic credentials. It
will distinguish that DMS contract from direct CMS `/connect/token` use and
will not imply support for form-body `client_id` or `client_secret`.

### Public OpenAPI OAuth URL

The frontend metadata layer owns client-visible URLs. Reuse Discovery's
request-derived route-prefix logic to construct the public DMS OAuth proxy URL:

```
Request.RootUrl() + tenant/route-qualifier prefix + "/oauth/token"
```

This preserves PathBase, actual route values, and placeholders where Discovery
uses them. Use the value when loading the file-backed Discovery specification.
For the Resource, Descriptor, Change-Queries, and profile Resource generated
OpenAPI documents, replace the client-credentials security scheme's `tokenUrl`
in the response document before serialization.

The endpoint receives response-local deep clones from `IApiService`, so this
frontend mutation neither changes cached specifications nor expands the
`IApiService` interface. Core `ApiService` continues to use
`AuthenticationService` for internal runtime behavior; the metadata endpoint
only prevents that internal Docker hostname from reaching clients.

### Bootstrap output and safe path logging

For a wrapper-owned normal run (not the terminal `-InfraOnly` workflow), pass
the existing full writer-guidance suppression to the initial
`start-local-dms.ps1 -InfraOnly` call. The wrapper subsequently configures,
provisions, and invokes `-DmsOnly`, so it must not print "DMS service was not
started" or manual IDE next steps first. Direct/manual `start-local-dms.ps1
-InfraOnly` and wrapper terminal-IDE flows retain their guidance.

At the prepared ApiSchema workspace log site, replace `Format-LogSafeText` with
the existing `Format-LogSafePath`. The latter removes control characters but
retains legal Windows path characters, including backslashes.

## Components and data flows

| Concern | Components | Resulting flow |
| --- | --- | --- |
| Local rebuild | `bootstrap-local-dms.ps1` -> `bootstrap-wrapper.psm1` -> `start-local-dms.ps1` | Explicit `-Rebuild` forwards to the existing no-cache local image build; omitted switch does not build. |
| Public OAuth metadata | `DiscoveryEndpointModule`, `MetadataEndpointModule`, `ContentProvider`, `IApiService` response values | Request URL and route context produce a public DMS proxy URL; every served OpenAPI response advertises it while forwarding stays internal. |
| Normal bootstrap output | `bootstrap-wrapper.psm1`, `start-local-dms.ps1` | Wrapper suppresses terminal InfraOnly writer output only on its normal continuation path, then performs DMS-only startup. |
| Safe logging | `prepare-dms-schema.ps1`, `bootstrap-manifest.psm1` | Prepared workspace path is control-character sanitized without losing backslashes. |
| Operator guidance | `GETTING_STARTED.md` | Normal bootstrap, explicit local rebuild, and Basic proxy instructions align with script/runtime behavior. |

## Failure handling and regression safeguards

- Rebuild failure continues to surface the existing Docker build error and
  stops the wrapper before configure/provision/DMS-only work.
- A stale workspace, package identity mismatch, effective-schema mismatch, or
  fingerprint mismatch continues to fail before Docker/CMS side effects; this
  design does not automatically replace workspace content.
- No database migration or runtime compatibility behavior is introduced.
- Metadata URL generation must retain PathBase and tenant/qualifier behavior;
  it must not emit `AuthenticationService` or a container hostname to clients.
- Manual InfraOnly output is a compatibility contract. Suppression is limited
  to the wrapper's normal path, not direct command use or terminal IDE flows.
- The DMS OAuth proxy remains HTTP Basic-only for client credentials; existing
  malformed-header/error behavior remains unchanged.

## Acceptance mapping and verification

The story has no explicit acceptance criteria. These synthesized requirements
cover each distinct EDFI-2881 observation.

| Requirement | Design | Meaningful verification |
| --- | --- | --- |
| REQ-1: image upgrade path | Public local-wrapper rebuild pass-through; focused normal-bootstrap/rebuild guidance | Extend `BootstrapEntryPointWorkflow.Tests.ps1` to prove `-Rebuild` reaches only initial local startup, default does not; review the documented normal and rebuild commands. |
| REQ-2: database migration feedback | No change | No migration, compatibility detector, runtime test, or user-facing documentation is added. |
| REQ-3: client-reachable OAuth metadata | Request-derived public proxy URL applied to Discovery plus all generated OpenAPI responses | Frontend metadata tests cover unqualified, PathBase, and tenant/route-qualified requests; assert exact public `tokenUrl` and absence of the configured internal hostname for Resources, Descriptors, Change Queries, profiles, and Discovery. |
| REQ-4: CMS health observation | No change | Preserve existing health response and tests; no consumer requirement justifies a contract change. |
| REQ-5: DMS proxy credentials | Getting-started Basic/proxy clarification only | Retain existing `TokenEndpointModuleTests` Basic and malformed-header behavior; inspect the guide's Discovery-to-Basic flow. |
| REQ-6: Windows path logging | Use `Format-LogSafePath` at the prepared-workspace call site | Extend `BootstrapSchemaDeploymentSafety.Tests.ps1` with a printable Windows path containing backslashes/punctuation and a control character; assert path preservation and control removal. |
| REQ-7: misleading normal-bootstrap guidance | Full writer-guidance suppression for wrapper normal path only | Extend `BootstrapEntryPointWorkflow.Tests.ps1` to prove normal wrapper output omits the terminal InfraOnly block but reaches DMS-only startup, while direct InfraOnly guidance remains. |

## Dependencies, deviations, and blockers

Relevant design constraints are DMS-1151 schema/workspace safety,
DMS-1153's phase ownership and terminal InfraOnly behavior, and DMS-1271's
ownership of guarded workspace replacement. DMS-1428 is the only identified
stored epic sibling but concerns Document Cache administration and has no
dependency on this story.

There are no remaining design blockers. Implementation planning may begin only
after the user approves this written specification.
