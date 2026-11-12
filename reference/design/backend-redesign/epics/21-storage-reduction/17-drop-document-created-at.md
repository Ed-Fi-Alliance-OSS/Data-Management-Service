---
jira: DMS-1403
jira_url: https://edfi.atlassian.net/browse/DMS-1403
epic: DMS-1402
status: v3.1 — approved for Phase 0 on 2026-10-12 (Codex review 4 technical approval, Q1 closed by product, user approval; §0.4); implemented on branch `DMS-1403` (draft PR #1359); qualification and release gates remain open (§8.3, §7.5)
---

# Story: Drop `dms.Document.CreatedAt`

Implementation spec, phased, for review before any code change.

Branch `DMS-1403` at `205d5e98b` (= `origin/main` on 2026-10-12), worktree
`C:\dev\ed-fi\Data-Management-Service\src\Data-Management-Service-DMS-1403`. The worktree carries a
pre-existing, unrelated modification to
`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/packages.lock.json`; every commit in
this plan stages files explicitly and never includes that file.

Sibling state on 2026-10-12: DMS-1404 (compact descriptor IDs) is **Done**, merged as PR #1343 on
2026-10-09 and already in this branch's base. DMS-1401 (drop `ContentLastModifiedAt`) is **In
Progress** in Jira. On 2026-10-12 it had no branch, worktree, or PR in this repository. **Corrected
2026-10-14:** DMS-1401 is now PR #1356 (branch `DMS-1401`, head `171094676` on 2026-10-14, opened
2026-10-13, not merged). It overlaps this story in most files and has semantic conflicts with it
(§7.6). DMS-1398 (the spike) is Done. DMS-1402 (epic) is Open.

## 0. Review dispositions

### 0.1 Review 1 (Codex, 2026-10-12)

| Finding | Disposition |
|---|---|
| P1 Compatibility/CDC rollout claims too broad | §7.2 split into API CRUD compatibility vs CDC setup/validation compatibility; §7.4 rewritten (and corrected again in review 2, below); validation is not weakened; PAC-4 and PAC-10 cover it. |
| P2 Inventory incomplete | `CdcSqlServerHeartbeatDatabaseProviderTests.cs:363` added to Phase 1 (T-A); `PostgresqlDescriptorProjectionAliasTests.cs:176` and `PostgresqlDescriptorProjectionPipelineTests.cs:153` added to T-B; the classified sweep was repeated over all 80 `.cs` files that name `CreatedAt` (§5 method); "only non-test code" replaced by "no application data-path reader" with the operational-tooling reader named. |
| P3 Checkpoints not buildable | Phase 1 is one commit (definition + its expectations + goldens). Phase 2 is split by shared record, each commit updating the record and its consumers on both engines, so every commit compiles and its fixtures run. Phase gates corrected. Stop-and-review after every step retained. |
| P4 Template verification not binding | Step 5.3 is mandatory (Q3 = yes) and fail-closed; four states tested on both engines through the function's real engine branches; release handoff recorded (§7.5) without assuming DMS-1401's rebuild includes this change. |
| P5 Matrix not executable | §8 has exact PowerShell commands, `--results-directory` + `--logger trx`, result paths under `C:\dev\ed-fi\reviews\DMS-1403\`, a baseline-capture step (0.3), formatting scoped to changed files, and local pre-push gates separated from CI gates. |
| Q1 | Still pending product confirmation; the spec remains conditional (§1.5). |
| Q2 / Q3 / Q5 | Yes; mandatory steps (2.7, 5.3, 0.2). |
| Q4 | Optional; if run, identical data/load on both engines and per-engine storage components labelled (6.1). |
| Jira write in 0.2 | Removed. Jira "Implementation Decisions" text is drafted for the user to post; this workflow authorizes local commits only. |
| "Mechanical rebase" | Replaced: after DMS-1401 lands, re-check every PAC and regenerate evidence (§7.6). |

### 0.2 Review 2 (Codex, 2026-10-12)

| Finding | Disposition in v3 |
|---|---|
| P1 "retire → reprovision → re-enable" is not a supported CDC upgrade path | Verified against `design-docs/cdc/cdc-streaming.md` ("V1 physical-source replacement deferral": no source-replacement command, no replacement-driven restore/copy or new-generation capture setup, operator-prepared replacement databases excluded; initial enablement rejects unexpected canonical/cache/work rows before capture setup). The procedure is removed from §7.4, PAC-6, step 5.1, §10, and §0.1. §7.4 now distinguishes (a) fresh provisioning with eligible initial CDC enablement, (b) non-CDC reprovisioning/copy-forward, and (c) existing established CDC deployments, whose upgrade/cutover is **unsupported** and is recorded as an unresolved delivery limitation in the release handoff (§7.5). DMS-1403 does not implement CDC replacement. |
| P2 PAC-10 expected two diagnostics that cannot co-occur | Verified: `CdcSqlServerHeartbeatDatabaseProvider.BuildSetupSteps` orders `ExecuteSourceInventoryAsync` before `ExecuteCaptureInstancesAsync`, and `CdcProviderSetupService.SetupAsync` breaks on the first error diagnostic. PAC-10, §7.2, step 1.1, and step 3.2 now treat the two as **separate rejection mechanisms**: live tests on both engines prove rejection at source inventory from otherwise valid prerequisites; the capture-column mismatch is proven through the existing provider unit-test seam with a matching source inventory so execution reaches capture inspection. Production validation order is untouched. |
| P2 Filters were filename fragments; count check could hide omissions | §8.4 maps every affected fixture to its actual class name (verified by listing `class Given_…` declarations per file); each step's filter is built from that list; §8.2 now requires **each** expected fixture to have `Passed > 0` and `Failed = 0` by exact name, requires the new tests by name, and adds a `--list-tests` discovery pre-check. |
| Revert detection overstated | §9.1 reclassified: physical removal is established by the emitter assertions, the generated goldens, the live catalog goldens, and the live source-inventory tests; seeder pins, Northridge text pins, and the mocked verifier tests are labelled as component checks. |
| Verifier AST check proves syntax only | Replaced by a script-level mocked Pester test: a source-database failure prevents the restore, and a restored-database failure prevents the serveability probe (step 5.3, case group f). Engine-specific function tests (a–e) retained. |

### 0.3 Review 3 (Codex, 2026-10-12)

Architectural blockers resolved, including the CDC disclosure (§7.4 c / §7.5 item 2 are to be
preserved verbatim in implementation docs and the PR; technical approval of this ticket is not
approval to upgrade established CDC deployments). Two bounded corrections applied in v3.1:

| Finding | Disposition |
|---|---|
| §8.4 list 2.1 omitted the seeder's actual direct consumers | Verified: the only integration files referencing `MaterializedDocumentFixtureSeeder` are the four `DocumentCacheMaterializer{Fixture,LinkBearingResource}Tests` files (fixtures `Given_Postgresql_DocumentCacheMaterializer_Fixtures`, `Given_Postgresql_DocumentCacheMaterializer_LinkBearingResource`, `Given_Mssql_DocumentCacheMaterializer_Fixtures`, `Given_Mssql_DocumentCacheMaterializer_LinkBearingResource`). The six names previously listed do not use the seeder (they are T-B mirror-table fixtures already covered by 2.7) and are removed from 2.1. The unit-level consumer `CompactDescriptorFixtureSeedTests` is added to U3. |
| Step 0.3 required discovery of fixtures that do not exist yet | Baseline discovery (0.3) now covers only fixtures that exist at the baseline commit; the four new fixtures from 1.1 and 3.2 are discovered and shown passing in their own implementation step before commit. The baseline commit (`205d5e98b`) is recorded separately from `HEAD`, since 0.2 adds a documentation commit first. |

**Status after review 3:** approval pending **Q1 only** plus confirmation of these two
corrections. No implementation has started.

### 0.4 Review 4 and approval (2026-10-12)

Codex review 4 technically approved v3.1 with no blocking findings. Its conditions stand: per-step
local commit and review gates, `RelationalMappingVersion` kept at `v3`, the frontend
`packages.lock.json` change kept out of every commit, explicit approval before any push, and
technical approval of this ticket is not approval to upgrade established CDC deployments.

Q1 is closed "no" (§1.5). The user then approved v3.1 for Phase 0 with scope unchanged and
recorded the decisions in the DMS-1403 Jira description ("Implementation Decision" section,
2026-10-12). The Jira text that step 0.2 was to draft is therefore already posted; this workflow
still performs no Jira write.

### 0.5 Baseline review (step 0.3, 2026-10-13)

The 0.3 baseline was accepted. Two recording rules follow from it. First, the parameterized
fixture `Given_Compact_Descriptor_Fixture_Seeds` is required as `…(Postgresql)` and `…(Mssql)`
independently, because neither the test listing nor the `.trx` reports the bare class name (§8.2,
§8.3 U3, §8.4 list 2.1). Second, a later failure is classified as pre-existing only when both its
test name and its failure signature match the baseline record (`baseline\failing-signatures.txt`),
not on the name alone.

Found while implementing step 1.1 (pending review): the golden regeneration also rewrites the
`normalized_sql_sha256` pair in 24 DDL manifest JSONs, which §5.5 had listed as not affected. The
change is derived from the SQL goldens and carries no other edit; §5.5 and step 1.1 (files and
diff-shape gate) now include it.

Found while implementing step 2.2 (approved 2026-10-13): the `ParityScenarioCatalog.*.cs` method
lists are resolved by reflection in `Given_The_Postgresql_Parity_Catalog_Resolution`,
`Given_The_Mssql_Parity_Catalog_Resolution`, and the backend unit fixtures
`Given_The_Parity_Scenario_Catalog` and `Given_The_Parity_Catalog_Entry_Point_Resolution`, so a
removed test method must also leave the catalog. §8.4 lists 2.2 and 2.3 now require these four
fixtures by exact name in their own projects, and step 2.2 states that its catalog edit removes
the obsolete method references. Step 2.2's recorded evidence already passes the added gates
(1, 1, 24, and 3 passed; no rerun).

Found while implementing step 2.5 (approved 2026-10-13): the ten list-2.5 query-authorization
fixtures never call the authorization side-effect readers, so they pass with or without the
change. They stay in list 2.5 as regression coverage for the edited files. List 2.5 now also
requires the five PostgreSQL and six SQL Server fixtures that do exercise those readers (§8.4
list 2.5), by exact full name. Step 2.5's recorded evidence already satisfies the added gates
(71 of 71 and 96 of 96 passed). With the step files reverted, 52 and 70 tests failed on the
missing column, which shows the readers' consumers now match the six-column schema. No rerun was
needed.

Found while implementing step 2.6 (approved 2026-10-13): `ReadDocumentRowAsync` in
`PostgresqlDescriptorReadTestSupport` and `MssqlDescriptorReadTestSupport` is also called by
`Given_A_Postgresql_DescriptorRead_Get_Request` and `Given_A_Mssql_DescriptorRead_Get_Request`.
List 2.6 now requires both by exact full name in their own projects (§8.4 list 2.6). Step 2.6's
recorded evidence already satisfies the added gates (7 of 7 passed on each engine). With the step
files reverted, 2 tests failed per engine on the missing column. No rerun was needed.

Found while verifying Phase 2 (approved 2026-10-13): the Phase 2 and Phase 3 `git grep` gates
did not name the step 1.1 test cases that must keep naming the column. These are the legacy
seven-column source-inventory and capture-instance fixtures, and the `CoreDdlEmitterTests`
assertions that the column and its default are absent. Both gates now list them as permanent
exceptions E1–E3, scoped to those test cases rather than their files. Phase 3 adds the two 3.2
legacy-shape fixtures as E4–E5. The Phase 2 gate keeps the step 3.1 CDC files as unfinished
references, separate from the exceptions. This is a wording change only: Phase 2's recorded
grep output already matches it, and no rerun was needed.

Found while reviewing step 3.1 (approved 2026-10-13). There are two evidence corrections and no
code or workflow change.

- **I7 classification.** The I7 row said Kafka/Connect fixtures self-skip, but Kafka qualification
  prerequisites can fail `OneTimeSetUp` rather than skip. Step 3.1's I7 run had 557 passed,
  53 failed, and 1,174 not executed. The 53 failures are 50 Kafka qualification fixtures failing
  with `Docker operation failed (image)` and three Windows journal-interruption failures
  (`CdcWorkflowStateException`, state `Unavailable`). The 1,174 not-executed tests are 964 CDC
  message-contract tests whose prerequisite `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` is missing,
  200 pinned-image smoke tests whose prerequisites are not configured, and 10 marked unsupported
  on Windows. I7 had no 0.3 baseline. All 53 failures were reproduced with identical names and
  signatures on the **pre-step-3.1 tree** (`63f4e0989`, the 3.1 revert run), not on the 0.3
  baseline. The I7 row (§8.3) now says this. Classification still compares name and signature,
  and there are no blanket failure exclusions.
- **Qualification runs.** "Pending CI" was not a plan. Ordinary PR CI runs the CDC integration
  assembly with `Category!=DatabaseIntegration` only, and the connector smoke job
  (`run-backend-cdc-connector-template-smoke-tests`) runs only on `workflow_dispatch`. So a green
  PR build covers neither the pinned-image smoke nor the message contract. §8.3 now records three
  required qualification runs against the reviewed branch SHA before merge:
  - I7-smoke, from `on-dms-pullrequest.yml` dispatched on the branch.
  - I7-contract, from `nightly-cdc-qualification.yml` dispatched with
    `lane=Postgresql, suite=MessageContract`.
  - I7-contract again, with `lane=Mssql, suite=MessageContract`.

Found while reviewing step 4.1 (approved 2026-10-13): the P1 row said live scenarios self-skip,
but `NorthridgeFailClosedGuards.Tests.ps1` has no live scenarios. Its three skips are
environment-dependent: one needs symbolic-link privilege and two need a POSIX `sh` on `PATH`. The
P1 row now says this. This is a wording change only. Step 4.1's recorded run (129 passed,
3 skipped, the same three as the 0.3 baseline) already matches it, and no rerun was needed.

Found while implementing step 4.2: from Windows `pwsh`, the live suite cannot get past the copy.
The tool pipes its COPY-header rewrite script into `docker exec -i … sh -s`, and Windows `pwsh`
ends piped text with CRLF. The script's last line, `grep -e "$header" "$in"`, then passes the file
name with a trailing carriage return, and the copy stops at the staging step (`grep:
/tmp/northridge-document.sql…: No such file or directory`). The cause is in how Windows pipes
text, not in this story's change; the Pester lane runs on ubuntu. P2 is therefore run from Linux
`pwsh` (§8.3 P2). Step 4.2 also narrows the existing exact `dms.Document` value comparison to the
target's columns, compared by name, because a whole-row comparison cannot hold once the source
row has one more column (§6 step 4.2).

Found while reviewing step 4.2 (Phase 4 approved 2026-10-13): the 5.1 documentation grep said
"only tracked-change references", which the required removal explanation, the §7.3 catalog
queries, and the limitation statements would all violate. Step 5.1's gate now classifies every
hit into one of four allowed kinds and forbids only a description of the column as current. It
also covers `eng/northridge/README.md`, whose copy-forward wording step 4.1 already updated, so
5.1 does not edit that file again; `EPIC.md` was done in 0.2. The sentence that the template
verifier rejects older packages moves from 5.1 to 5.3, the step that adds the check.

Found while preparing step 5.2 (coordination approach approved 2026-10-14): DMS-1401 is now
PR #1356, opened 2026-10-13. This spec had said it had no branch or PR (header, §1.4 A4, §7.6),
and §6 step 5.3 assumed DMS-1401 would add `ContentLastModifiedAt` to this story's
`-ForbiddenColumns`. Neither holds. #1356 ships its own template catalog assertion, and its
Northridge tool requires and reads `CreatedAt`. The header, A4, step 5.2, the step 5.3 design
note, §7.6, and §10 are corrected. §7.6 now names four semantic conflicts. It also sets the
integration rules: consolidation preferred, two deliberate checks acceptable, DMS-1401's
validations preserved, combined-tree rejection evidence for either removed column, and diff-shape
gates measured against the integration base. The full PAC recheck is retained. Step 5.3 is
implemented as designed, without importing or waiting for #1356. This is a spec correction only,
with no code change.

Found while implementing step 5.3. These are small departures from the design text. None changes
the call points or the fail-closed semantics.
- **`SET NOCOUNT ON`.** The SQL Server query starts with it, as `Get-UserSchemaNames` already does.
  Without it, sqlcmd prints `(N rows affected)`, which would be parsed as a column name. A missing
  table would then report "unexpected shape" instead of "missing". §6 step 5.3 now shows the
  prefix.
- **Database name.** The function rejects an unsafe database name before running any query.
- **Comparison and parameters.** Forbidden columns are compared case-insensitively and required
  columns case-sensitively; both are the stricter choice. `-DatabaseEngine` is mandatory.
- **Default runner.** It is an inline branch rather than a default scriptblock, so
  PSScriptAnalyzer can see that `-ContainerName` and `-MssqlPassword` are used. The
  `-QueryRunner` seam is unchanged.
- **Tests beyond (a)–(f).** The suite adds a case-variant forbidden column, a two-column
  `-ForbiddenColumns` list (the combined-tree shape), a missing required column, and an unsafe
  database name. (f3) also asserts the call order: source check, then restore, then restored
  check, then CMS token.

Round 2 review of `f14ac1f2c` (2026-10-15): after DMS-1401 integration, the current
`dms.Document` has five columns, so the current-shape statements (header status, PAC-4, PAC-10 b,
§7.1, §7.2, §7.4, §7.5, I8, §9.1) now say five. The step 1.1–3.2 text and the dated notes above still
describe the six-column shape at `205d5e98b`; they are history. PAC-10 (b) now states its
evidence exactly: five-column source inventory, seven legacy captured columns, five expected
captured columns. The seven-column capture fixture keeps its name. The legacy source-inventory
fixture is renamed `Given_A_Legacy_Document_Inventory_With_CreatedAt`, because the current
column count plus `CreatedAt` is six, not seven. Its no-op `CreatedAt` filter is removed. The
§6 step 1.1 filter, E1, and §8.4 list 1.1 use the new name.

---

## 1. Problem, proposed decision, alternatives, assumptions, open questions

### 1.1 Problem

`dms.Document` carries `CreatedAt` (`timestamp with time zone NOT NULL DEFAULT now()` on
PostgreSQL, `datetime2(7) NOT NULL CONSTRAINT DF_Document_CreatedAt DEFAULT (sysutcdatetime())` on
SQL Server). It is the seventh and last column of the table. The Jira description states the
column has no production reader and costs eight bytes of raw payload per document. This spec's
inventory (§5) confirms against the current tree that there is **no application data-path
reader**: no handler, repository, materializer, trigger body, CDC connector template, cache path,
or CLI reads or writes it. The column is named by the shared table definition that emits it, by an
unreferenced column-name constant, by the CDC source inventory derived from the definition (and the
tests that pin that inventory), and by one **operational tooling reader**: the Northridge
copy-forward's stamp-distribution comparison (§5.2).

### 1.2 Proposed decision

**Remove `dms.Document.CreatedAt` from the generated physical schema on both engines and add no
replacement.** Approved on 2026-10-12 after the product confirmation in Q1 below (§0.4). The
DMS-1403 Jira "Implementation Decision" section records the selected option and its scope.

### 1.3 Alternatives considered (from the Jira description) and why they are not proposed

| # | Alternative | Per-row cost | Why not proposed now |
|---|---|---|---|
| A | Derive creation time from the DMS-generated UUID (`FastGuid.NewPostgreSqlGuid()` in `UpsertHandler.cs:58`; FastGuid exposes `PostgreSql.GetTimestamp(Guid)`). | 0 | Not an audit timestamp: the UUID is minted in the API process before the transaction, so it is process-clock time, not database insertion time; a client-supplied or imported UUID (bulk loads, copy-forward of legacy dumps, `OVERRIDING SYSTEM VALUE` seeders) carries no DMS timestamp at all; SQL Server uses the same PostgreSQL-ordered GUID today (Jira aside: "SQL Server needs to start using its own FastGuid call" is a separate concern). Deriving a time from the UUID is a free, best-effort forensic hint that needs no schema; it is not something this story should promise as a capability. |
| B | `CreatedAtOffsetSeconds int` relative to a datastore epoch | 4 | Requires a new epoch column/table, new write-path code, new readers, and a migration concept; halves rather than removes the cost; nobody has asked for second-precision creation time. |
| C | Creation batch/bucket id + bucket table | 4 + table | New table, new write-path batching contract, new APIs; same objection as B. |
| D | Sparse `DocumentCreationCheckpoint` by `DocumentId` range | ~0 | Approximate only; new table and maintenance process; `DocumentId` is insert-ordered but copy-forward preserves legacy ids, so ranges are not time-monotonic across a migrated store. |
| E | Optional, physically separate append-only audit table | 0 when off | Adds an opt-in feature with triggers/retention semantics; no requirement exists. |

All of B–E are listed in Jira as options "if we still want to derive document creation time for
auditing". The spec therefore pivots on Q1. If Q1 answers "yes, creation time is required", this
spec is withdrawn and a new one is written for the chosen alternative; the removal work in this
spec is not a prerequisite for any of them.

### 1.4 Assumptions

- A1. No product, API, security, or operational feature requires document creation time (Q1).
- A2. The `_lastModifiedDate`/`_etag` contract, change queries, ownership authorization, cache
  coherence, representation restamping, and no-op/rollback semantics never consult `CreatedAt`
  (verified by inventory; re-verified by the behavior suites in Phase 2).
- A3. Existing databases are not migrated in place. As with DMS-1404, the physical change takes
  effect only in databases provisioned from the new DDL; `RelationalMappingVersion` stays `v3`
  and `EffectiveSchemaHash` does not change, so **DMS startup validation** cannot detect an old
  physical schema (§7.3). CDC setup/validation **can** and does fail closed on it (§7.4), and
  there is no supported path to move an established CDC deployment onto the new shape (§7.4 c).
- A4. DMS-1401 (PR #1356, open on 2026-10-14) changes the same files and has semantic conflicts
  with this story that a clean textual merge would hide (§7.6). Whichever story merges second
  re-checks every PAC and regenerates its evidence against the combined tree (§7.6). This story
  does not wait for DMS-1401, does not import its changes, and does not assume its design.
- A5. The branch contains DMS-1404, so every fixture and tool in scope is on the compact-descriptor
  baseline. No legacy-descriptor mode is maintained.

### 1.5 Open questions and recorded answers

- **Q1 (blocking, product) — closed "no" (2026-10-12).** Is there any required creation-time
  audit capability for documents (API field, report, support procedure, data-retention rule, or
  Admin/Config feature)? Answer: no. Source: Brad Banister approved dropping
  `dms.Document.CreatedAt` without a replacement creation-time capability, as relayed by the user
  during the DMS-1403 review on 2026-10-12 and recorded in the DMS-1403 Jira "Implementation
  Decision" section. That record reports the approval; it is not a verbatim quotation. None of
  B–E is selected.
- **Q2 — answered yes (review 1).** Align every test-local mirror table named `dms."Document"`
  (category T-B in §5.4) to the new shape in this story (step 2.7).
- **Q3 — answered yes (review 1).** Add the `dms.Document` column-shape assertion to
  `eng/DatabaseTemplates/verify-template-restore.ps1` with fail-closed semantics and Pester
  coverage (step 5.3). DMS-1401's eight-leg template rebuild/restore matrix and consumer-pin
  updates are **not** imported as DMS-1403 gates; the release handoff in §7.5 records what must
  happen against the final 8.1 packages.
- **Q4 — optional (review 1).** The storage measurement (6.1) may be run; if it is, both engines
  use identical synthetic data and load conditions, and each engine's measured components are
  labelled. The projected combined ratio from Jira is not an acceptance target and is not claimed.
- **Q5 — answered yes (review 1).** Commit the revised spec as
  `reference/design/backend-redesign/epics/21-storage-reduction/17-drop-document-created-at.md`
  and link it from `EPIC.md` after approval (step 0.2). No Jira write is performed by this
  workflow; the Jira "Implementation Decisions" text is drafted for the user.

---

## 2. Scope and non-goals

### 2.1 In scope

1. Remove `CreatedAt` from the shared `dms.Document` definition
   (`DmsCoreTableDefinitions.Document`) so core DDL, the CDC source inventory, and every consumer
   derived from that definition stop emitting or expecting it, on PostgreSQL and SQL Server.
2. Remove the unreferenced `DocumentCacheInventoryDefinition.DocumentColumns.CreatedAt` constant.
3. Regenerate every checked-in golden that is derived from the definition: 18 PostgreSQL DDL
   goldens, 18 SQL Server DDL goldens, and the two SchemaTools provisioned-schema catalog manifests.
4. Update test expectations that pin the column: DDL emitter unit tests, CDC source inventory unit
   tests, the SQL Server CDC provider unit test's captured-column list, SQL Server capture-column
   expectations (backend and SchemaTools live tests), the CDC connector pinned-image fixture, the
   materialized-document fixture seeder and its unit tests, the document-state snapshot records
   and SQL used by the authorization side-effect, guarded no-op, POST-as-update, atomic-rollback,
   descriptor-read, and query-authorization integration tests on both engines, and the test-local
   mirror tables (Q2).
5. Add explicit evidence of the two existing CDC rejection mechanisms for a physical
   `dms.Document` shape that disagrees with the runtime's expected inventory: source-inventory
   rejection (unit + live, both engines) and SQL Server capture-column mismatch (unit, provider
   seam). No production validation order or rule changes.
6. Update `eng/northridge/Copy-NorthridgeDataForward.ps1` (`Get-StampDistribution`) so it no
   longer reads `dms.Document.CreatedAt`, keep every other integrity check, and prove with Pester
   that an older dump that still carries `CreatedAt` copies into a target that lacks it.
7. Add a fail-closed `dms.Document` column-shape assertion to the template restore verifier (Q3),
   with function-level and script-level Pester coverage.
8. Update design and operator documentation: `data-model.md`, `RELATIONAL-BACKEND.md`
   (reprovisioning, catalog verification, template compatibility, CDC limitation), Northridge
   README, 8.1.0 changelog, and the epic index.
9. Keep `SchemaHashConstants.RelationalMappingVersion = "v3"`.

### 2.2 Non-goals (explicitly out of scope)

- Any replacement for creation time (UUID derivation API, offsets, buckets, checkpoints, audit
  tables, new APIs, new configuration).
- `dms.Document.ContentLastModifiedAt`, its ownership, stamping triggers, restamping, and every
  content-stamp/concurrency semantic (DMS-1401).
- Every other column named `CreatedAt`: tracked-change tables (`TrackedChangeSystemColumnRole.CreatedAt`),
  `dms.RepresentationRestampOperation.CreatedAt`, CMS `dmscs.*` audit columns, OpenIddict tables,
  jobs, CDC workflow journal timestamps. None of these change.
- In-place migration of existing databases.
- **Any CDC physical-source replacement, cutover, adoption, or re-enablement procedure for an
  established CDC deployment.** The CDC design defers physical-source replacement; this story
  neither implements it nor documents a workaround (§7.4 c). The limitation is disclosed.
- Template package rebuilds, publication, release-view promotion, or consumer pin updates (§7.5).
- Weakening `CdcSourceInventoryValidator`, the SQL Server capture-instance comparison, or the
  provider setup-step order / first-error stop.
- Provider-specific GUID generation for SQL Server.
- Any change to document ids/UUIDs, `ResourceKey`, change versions, ETag/last-modified responses,
  cache coherence, ownership authorization, or no-op/rollback behavior.
- Repository-wide renames or formatting; only files in the inventory are touched.
- Writes to Jira or any other external system from this workflow.

---

## 3. Proposed acceptance criteria (PAC)

Jira has no acceptance-criteria list. These are **proposed** criteria derived from the ticket text
("drop ... CreatedAt", "no production reader", "eight bytes per document", "needs measurement"),
the epic's established delivery pattern (DMS-1404: fresh-schema DDL on both engines, regenerated
fixtures, tooling, reprovisioning documentation), and the boundaries in the assignment. They
become binding only when approved.

- **PAC-1 Physical removal on fresh schemas.** Generated PostgreSQL and SQL Server DDL for every
  fixture and for authoritative DS 5.2/5.2-TPDM/sample no longer defines `dms.Document.CreatedAt`
  or `DF_Document_CreatedAt`. A database provisioned by `ddl provision` on each engine reports no
  such column in its catalog (SchemaTools provisioned-schema goldens). All other `dms.Document`
  columns, defaults, constraints, and indexes are byte-for-byte unchanged in the goldens.
- **PAC-2 No code dependency.** No application code, CLI, trigger body, connector template, or
  shared test seeder references `dms.Document.CreatedAt`; `DocumentColumns.CreatedAt` is removed;
  the solution builds.
- **PAC-3 Behavior preserved.** On both engines the existing integration suites for authorization
  write side effects, guarded no-ops (profile and no-profile), POST-as-update, atomic rollback,
  hydration, descriptor reads/projection, document cache source metadata/materializer coherence,
  and query authorization pass with their surviving assertions intact and exact: `ContentVersion`,
  `ContentLastModifiedAt`, identities, rowsets, resource keys, and ChangeVersion allocation are
  still asserted by value. Removing the `CreatedAt` assertions does not weaken any other check.
- **PAC-4 CDC alignment.** The CDC source inventory for `dms.Document` has five columns in table
  order (`DocumentId`, `DocumentUuid`, `ResourceKeyId`, `CreatedByOwnershipTokenId`,
  `ContentVersion`; DMS-1401 removed `ContentLastModifiedAt`, §7.6); the SQL Server provider's
  generated `@captured_column_list` for `dms.Document` lists five; SQL Server capture-column
  expectations (backend and SchemaTools live tests) list five; the
  PostgreSQL publication and SQL Server capture integration tests pass; the connector pinned-image
  fixture's `Document` definition matches the inventory.
- **PAC-5 Copy-forward.** `Copy-NorthridgeDataForward.ps1` no longer reads
  `dms.Document.CreatedAt`; the stamp-distribution comparison still covers `ContentVersion` and
  `ContentLastModifiedAt` on `dms.Document`, descriptors, and sampled roots; an archive whose
  `dms.Document` COPY header carries `CreatedAt` is staged and inserted into a target without it,
  with `CreatedAt` reported source-only, and every fail-closed guard (unfillable NOT NULL,
  no-shared-column, duplicate column, malformed header) still throws. Evidence: the database-free
  Pester suite and the live PostgreSQL carry-forward suite.
- **PAC-6 Documentation and rollout.** `data-model.md` shows the new `dms.Document` on both
  engines; `RELATIONAL-BACKEND.md` states that the column is gone, that `RelationalMappingVersion`
  stays `v3`, that hash equality cannot prove removal, how to verify the catalog, that existing
  databases must be reprovisioned to realize the saving, that CDC setup/validation fails closed on
  a shape mismatch, and that **established CDC deployments have no supported upgrade/cutover path
  in this release** (separate release coordination); the Northridge README and the 8.1.0
  changelog are updated with the same limitation.
- **PAC-7 Unrelated columns untouched.** No tracked-change, restamp-operation, CMS, or other
  `CreatedAt` definition, golden, or test changes. Evidence: the golden diff contains only
  `dms.Document` lines; the relational-model manifests and derived-model-set fixtures are
  unchanged.
- **PAC-8 Honest storage claim.** The PR states the estimated raw reduction (8 bytes per
  `dms.Document` row before alignment/page effects) and either records the step 6.1 measurement
  (per-engine storage components before/after, same row count and load) or states that no
  measurement was taken. Jira's projected ratio is not claimed.
- **PAC-9 Gates.** CSharpier passes on changed C# files; the affected unit projects pass; the
  affected PostgreSQL and SQL Server backend integration fixtures pass and are shown to have
  executed **per expected fixture by exact name** (§8.2), not skipped; the new tests added by this
  story are shown executed by name; SchemaTools PostgreSQL/SQL Server golden tests pass against
  live engines; the bootstrap Pester lane suites touched here pass; environmental failures are
  reported separately from behavioral ones, and "pre-existing" failures are only those reproduced
  on the unchanged baseline (step 0.3).
- **PAC-10 CDC shape mismatch is rejected by two independent mechanisms (revised).**
  (a) *Source inventory:* with the new runtime, a database whose `dms.Document` still carries
  `CreatedAt`, and whose other CDC prerequisites are valid, is rejected by provider setup on both
  engines with `CDC_SOURCE_COLUMN_UNEXPECTED` for `dms.Document.CreatedAt` (`FailClosed`,
  `Outcome = Failed`). Evidence: validator unit test (1.1) and live tests on both engines (3.2).
  (b) *SQL Server capture columns:* when the source inventory matches the current five-column
  `dms.Document` shape and an existing `dms_binding_document` capture instance captures the seven
  legacy columns (the five current columns plus `ContentLastModifiedAt` and `CreatedAt`), the
  capture-instance inspection reports the artifact `Mismatched` against the five expected captured
  columns. Evidence: provider unit test
  `Given_An_Existing_Seven_Column_Document_Capture_Instance` through the existing fake-executor
  seam (1.1). The two mechanisms are not expected to fire in one invocation; the
  service stops at the first error. No validation rule, order, or stop behavior is changed.
- **PAC-11 Template verifier fails closed (new).** `verify-template-restore.ps1` asserts, for the
  source database before the restore and for the restored verification database before the
  serveability probe, that `dms.Document` exists and has no `CreatedAt` column; a missing table,
  a query failure, or a present `CreatedAt` each throw with the database name. Evidence:
  function-level Pester through the real engine branches with a mocked `docker` (a–e) and a
  script-level mocked Pester run showing that a source failure prevents the restore and a
  restored-database failure prevents the API probe (f).

---

## 4. Traceability matrix

| Id | Requirement / criterion | Source | Implementation steps | Verification evidence |
|---|---|---|---|---|
| TR-1 | Drop `dms.Document.CreatedAt` (no smaller alternative) | Jira title + §1.2 decision | 1.1, 3.3 | PAC-1 goldens diff (U1); catalog goldens (I5); catalog queries §7.3 |
| TR-2 | "No production reader" is true for the data path | Jira description | 1.1 (constant removal), §5 inventory | PAC-2 grep + build |
| TR-3 | Eight bytes per document; "needs measurement" | Jira description | 6.1 (optional) | PAC-8 PR statement |
| TR-4 | Both engines' DDL, manifests, CDC expectations, fixtures, seeders, snapshots | assignment, DMS-1404 pattern | 1.1, 2.1–2.7, 3.1–3.3 | PAC-1, PAC-3, PAC-4 (U1, U3, I1, I3, I5, I7) |
| TR-5 | Backend behavior tests keep exact checks on surviving stamps, identities, rowsets, rollback | assignment | 2.1–2.7 | PAC-3 per-fixture trx counts (§8.2, §8.4) and diff review |
| TR-6 | Copy-forward updated without weakening integrity | assignment | 4.1, 4.2 | PAC-5 Pester (P1 db-free + P2 live) |
| TR-7 | Template build/restore verification and docs on compatibility | assignment, review 1 P4, review 2 | 5.1, 5.3 | PAC-6, PAC-11 (P3); release handoff §7.5 |
| TR-8 | Retain unrelated `CreatedAt` columns; preserve `ContentLastModifiedAt` (DMS-1401) | assignment | all steps (negative scope) | PAC-7 diff review; U2 |
| TR-9 | Keep `RelationalMappingVersion = v3` | AGENTS.md, assignment | none (no change) | `git diff` of `SchemaHashConstants.cs` empty |
| TR-10 | Explain reprovisioning, catalog verification, template compatibility, CDC limitation | assignment, reviews 1–2 | 5.1 | PAC-6 doc review |
| TR-11 | Distinguish estimated vs measured savings | assignment | 6.1, PR text | PAC-8 |
| TR-12 | CDC rejection mechanisms are evidenced separately, not asserted jointly | review 1 P1, review 2 P2 | 1.1 (unit, both mechanisms), 3.2 (live, source inventory) | PAC-10 (U1, I1/I3 CDC fixtures by name) |
| TR-13 | Every commit compiles and its fixtures run | review 1 P3 | every step | per-step build + trx |
| TR-14 | Gates are executable; evidence is per expected fixture by exact name | review 1 P5, review 2 P2 | 0.3, §8.2, §8.4 | PAC-9; `C:\dev\ed-fi\reviews\DMS-1403\` |
| TR-15 | Established-CDC upgrade is disclosed as unsupported, not worked around | review 2 P1 | 5.1, 5.2, §7.4, §7.5 | PAC-6 doc review; handoff text in PR draft |
| PAC-9 | Gates and executed-not-skipped evidence | assignment | every step's verification | §8 matrix, trx counters |

---

## 5. Consumer inventory (current tree at `205d5e98b`)

Method: Rider `search_text` for `CreatedAt` over `src/dms` (capped at 1000 hits, so it was used
to list files, not to classify), then ripgrep over `src`, `eng`, `docs`, `reference`, `.github`
with per-file inspection. After review 1 the sweep was repeated: `rg -l CreatedAt src/dms --glob
'*.cs'` returns 80 files; every one is classified below as 5.1 (application), 5.3 (CDC), T-A,
T-B, or T-C. After review 2 the fixture class names of every T-A/T-B/CDC file were listed from
their `class Given_…` declarations (§8.4).

### 5.1 Application code (must change)

| File | Role | Change |
|---|---|---|
| `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/DmsCoreTableDefinitions.cs:51-57` | The one definition of the column; feeds `CoreDdlEmitter.EmitDocumentTable` (line 481), the CDC source inventory (line 199), and through it the SQL Server provider's `@captured_column_list` | Delete the column entry |
| `src/dms/backend/EdFi.DataManagementService.Backend.External/DocumentCacheInventoryDefinition.cs:39` | `DocumentColumns.CreatedAt`; **zero references** anywhere in the repo | Delete |

Verified **no** other application data-path reference: `Backend` (write/read/materializers),
`Backend.Postgresql`, `Backend.Mssql`, `Backend.Cdc` runtime (connector templates do not enumerate
columns; `CdcEstablishedValidation.cs:842` and `CdcWorkflowJournal*.cs` name the journal's own
`CreatedAt`), `Core`, `Frontend`, all CLIs, E2E tests, `src/config`.

Runtime consumers that are **derived** from the definition and therefore change behavior without a
code edit: `CdcSourceInventoryValidator.ValidateLiveSourceInventory` (`CdcSourceInventory.cs`,
invoked from `CdcProviderSetupAggregate.AddStepResult`, `CdcProviderSetupService.cs:267`) and the
SQL Server capture-instance inspection (`CdcSqlServerHeartbeatDatabaseProvider.cs`, the
`capturedColumnsMatch` comparison around line 3160, plus the `@captured_column_list` emitted at
enable time). The provider's `BuildSetupSteps` orders: source fingerprint → provider history →
database CDC → heartbeat table → **source inventory** → **capture instances** → grants → metadata
refresh; `CdcProviderSetupService.SetupAsync` stops after the first step that adds an error
diagnostic. These are the subject of §7.2/§7.4 and PAC-10.

### 5.2 Operational tooling (must change)

| File | Lines | Role | Change |
|---|---|---|---|
| `eng/northridge/Copy-NorthridgeDataForward.ps1` | 1109-1133 (`Get-StampDistribution`) | **The one operational reader:** builds `MIN/MAX("CreatedAt")` into the `dms.Document` stamp row that is compared source vs target | Drop the `CreatedAt` pair from the `dms.Document` SELECT; comment at 1106-1108 |
| same | 1244-1289 (`Resolve-StagedInsertColumn`), 1545-1548 (`Assert-CopyColumnShape` exempts `dms.Document`) | Already drop source-only `dms.Document` columns (`IdentityVersion` precedent) | No code change; new test coverage for `CreatedAt` as source-only |
| same | 1905-1908 | Requires `ContentVersion` and `ContentLastModifiedAt` on target | Unchanged |
| `eng/northridge/README.md:141-143` | Prose: "Both document stamp columns remain required" | Clarify `CreatedAt` is no longer read |
| `eng/northridge/tests/NorthridgeFailClosedGuards.Tests.ps1` | 737-760, 870-915 | Pins stamp-row shape `3|1|9|a|b|a|b` and a target column list containing `CreatedAt` | Update; add explicit `CreatedAt` source-only assertion |
| `eng/northridge/tests/CompactDescriptorCarryForward.Tests.ps1` | 40-70 | Live suite builds legacy source DDL from the compact fixture and seeds `dms.Document` with `CreatedAt` | Re-add `CreatedAt` to the *legacy source* DDL (simulating an older dump) and assert the target has none after copy |
| `eng/DatabaseTemplates/verify-template-restore.ps1` | 142-191 | No `dms.Document` column assertions today (only `Assert-CompactDescriptor.ps1` hooks and row counts) | Add the fail-closed column-shape assertion (step 5.3) |
| `eng/DatabaseTemplates/Template-Management.psm1` | 1599 (`Export-ModuleMember`) | Module the verifier imports | Add `Assert-DocumentTableShape` and export it |

All `.ps1`/`.psm1` files above are `eol=lf` in `.gitattributes` (`git ls-files --eol`), so edits
must preserve LF.

Not affected: `eng/docker-compose/setup-openiddict.ps1` (CMS tables), `eng/azure-vm`,
`.github/workflows` (template workflows call the verifier by path and need no change; the Pester
lane list gains one file in 5.3).

### 5.3 CDC (must change)

| File | Lines | Role |
|---|---|---|
| `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/CdcSourceInventoryTests.cs` | 327-347 | Pins 7 Document columns, ordinals 1..7 |
| `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/CdcSqlServerHeartbeatDatabaseProviderTests.cs` | 363 | Pins the provider's generated `@captured_column_list` for `dms.Document` with seven names; fails after 1.1 unless updated in the same commit |
| `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcProviderArtifactsTests.cs` | 868-880 | SQL Server capture-instance column expectation (live); fixture `Given_MssqlCdcProviderArtifacts` |
| `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCdcProviderArtifactsTests.cs` | — | Fixture `Given_PostgresqlCdcProviderArtifacts`; no `CreatedAt` text; gains the live source-inventory rejection fixture (3.2) |
| `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/CdcSqlServerHeartbeatDatabaseSchemaToolsTests.cs` | 796-808 | Same expectation through SchemaTools (live); fixture `Given_MssqlCdcHeartbeatDatabase_Provider_Setup` |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.cs` | 93-108 | Hand-written `Document` column list used to CREATE the smoke-test tables on both engines (2212-2234, 3050-3075) and compared against the inventory |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.MessageContract.cs` | 90, 101 | Parameter mapping arms that name `CreatedAt` (dead after removal) |

Not affected: `Backend.Cdc` runtime (no column lists), `src/dms/backend/Fixtures/cdc/**`,
Debezium connector JSON, `CdcQualifiedWorkerImage.json`, the nightly CDC history-parity workflow,
`CdcEstablishedValidation.cs` / `CdcWorkflowJournal*.cs` (journal `CreatedAt`).

### 5.4 Tests

**T-A — run against the generated schema or pin the derived inventory; would fail after removal
(must change):**

| File | What it does with the column |
|---|---|
| `Backend.Tests.Common/MaterializedDocumentFixtureSeeder.cs:189, 334, 343` | Creates `dms.Document` with the column when `CreateSchemasAndTables`, and INSERTs it in both modes (with `CreateSchemasAndTables=false` it targets a real provisioned schema) |
| `Backend.Tests.Unit/MaterializedDocumentFixtureSeederTests.cs:88, 146` | Pins the INSERT text on both dialects (`Given_MaterializedDocumentFixtureSeeder`) |
| `Backend.Tests.Common/ProfileGuardedNoOpTestSupport.cs:16, 72` | `ProfileGuardedNoOpDocumentRow.CreatedAt` read from the row (consumers: PG/MSSQL `ProfileGuardedNoOpTests`) |
| `Backend.Tests.Common/AuthorizationWriteSideEffectState.cs:14` | `AuthorizationDocumentState.CreatedAt` (consumers: PG/MSSQL `RelationalQueryAuthorizationTests`) |
| `Backend.Tests.Common/NoProfileGuardedNoOpScenarios.cs:128, 185, 224` | `DocumentRow.CreatedAt` + doc comments (consumers: PG/MSSQL `RelationalWriteGuardedNoOpTests`) |
| `Backend.Tests.Common/NoProfilePostAsUpdateScenarios.cs:152, 310, 463` | `DocumentRow.CreatedAt` + doc comments (consumers: PG/MSSQL `RelationalWritePostAsUpdateSmokeTests`) |
| `Backend.Tests.Common/NoProfileAtomicRollbackAssertions.cs:133` | `RejectedWriteDocumentRow.CreatedAt` (consumer: PG `...AuthoritativeSampleStudentSchoolAssociationSmokeTests`) |
| `Backend.Tests.Common/Parity/ParityScenarioCatalog.Profile.cs:525, 540, 555`, `...NoProfile.cs:437` | Scenario descriptions naming CreatedAt (text only) |
| `Backend.Postgresql.Tests.Integration/PostgresqlRelationalWritePostAsUpdateSmokeTests.cs:195, 672, 686, 2044, 2058, 3279, 3489, 3503, 4838, 4852` | SELECTs the column from `dms.Document`, builds rows |
| `Backend.Postgresql.Tests.Integration/PostgresqlRelationalWriteGuardedNoOpTests.cs:196, 394, 577, 591` | same |
| `Backend.Postgresql.Tests.Integration/PostgresqlProfileGuardedNoOpTests.cs:69, 715-718, 819-822` | SELECT + two `It_does_not_change_created_at` tests |
| `Backend.Postgresql.Tests.Integration/PostgresqlRelationalQueryAuthorizationTests.cs:2315, 2331` | SELECT |
| `Backend.Postgresql.Tests.Integration/PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs:4419, 4445` | SELECT |
| `Backend.Postgresql.Tests.Integration/PostgresqlDescriptorReadTestSupport.cs:125`, `...SupportTests.cs:150` | SELECT + `NotBeNull()` assertion |
| `Backend.Mssql.Tests.Integration/MssqlRelationalWritePostAsUpdateSmokeTests.cs:193, 678, 692, 1614, 1832, 1846, 3287, 3301, 5036, 5050` | mirror of PG |
| `Backend.Mssql.Tests.Integration/MssqlRelationalWriteGuardedNoOpTests.cs:207, 453, 639, 653` | mirror |
| `Backend.Mssql.Tests.Integration/MssqlProfileGuardedNoOpTests.cs:73, 496, 601` | mirror |
| `Backend.Mssql.Tests.Integration/MssqlRelationalQueryAuthorizationTests.cs:2820, 2836` | mirror |
| `Backend.Mssql.Tests.Integration/MssqlDescriptorReadTestSupport.cs:130`, `...SupportTests.cs:161` | mirror |
| `Backend.Ddl.Tests.Unit/CoreDdlEmitterTests.cs:1719-1723` | `It_should_have_named_default_for_document_created_at` |
| `Backend.Ddl.Tests.Unit/CdcSourceInventoryTests.cs:327-347` | see §5.3 |
| `Backend.Ddl.Tests.Unit/CdcSqlServerHeartbeatDatabaseProviderTests.cs:363` | see §5.3 |

**T-B — private mirror tables named `dms."Document"` created by the test itself; pass either way;
aligned in step 2.7 (Q2 = yes):**

PostgreSQL (9 files): `HydrationExecutorTests.cs` (8 blocks),
`PostgresqlDocumentReferenceLookupPageTests.cs:351`, `PostgresqlDescriptorProjectionTests.cs:185`,
`PostgresqlDescriptorProjectionAliasTests.cs:176`, `PostgresqlDescriptorProjectionPipelineTests.cs:153`,
`PostgresqlCollectionDescriptorProjectionTests.cs:209`,
`PostgresqlDocumentCacheSourceMetadataReaderTests.cs:54, 64`,
`PostgresqlDocumentCacheMaterializerDescriptorTests.cs:57, 79`,
`PostgresqlDocumentCacheMaterializerCoherenceTests.cs:54, 64`.
Shared: `Backend.Tests.Common/DocumentReferenceLookupPageFixture.cs:199, 302`.
SQL Server (8 files): `HydrationExecutorTests.cs` (6 blocks), `MssqlDocumentReferenceLookupPageTests.cs:374`,
`MssqlDescriptorProjectionTests.cs:136`, `MssqlDescriptorProjectionPipelineTests.cs:153`,
`MssqlCollectionDescriptorProjectionTests.cs:210`, `MssqlDocumentCacheSourceMetadataReaderTests.cs:61, 71`,
`MssqlDocumentCacheMaterializerDescriptorTests.cs:64, 86`, `MssqlDocumentCacheMaterializerCoherenceTests.cs:61, 71`.

**T-C — unrelated (no change):** everything that names tracked-change `CreatedAt`
(`DeriveTrackedChangeInventoryPassTests`, `IdentifierShorteningCollisionTests`,
`TrackedChangeTriggerBodyEmitterTests`, `AuthoritativeDdlGoldenTests:417-451`,
`DdlEmissionGoldenTests:3574-3618`, `ReadChangesAuthorizationPlannerTests`,
`TrackedChangeQueryPlannerTests`, `RelationalChangeQueryRepositoryTrackedChangesTests`,
`TrackedChangeQueryRequestHandlerTests`, `CompactDescriptorHistoryFixture`, `PgsqlDialectTests:812`,
`MssqlDialectTests:894`, `CoreDdlEmitterTests:2782`, `RelationalModelTypes.cs:869-871`,
`DerivedRelationalModelSetContracts.cs:386, 515`, `ApplyDialectIdentifierShorteningPass.cs:1996-2017`,
`DeriveTrackedChangeInventoryPass.cs`, `RelationalModelDdlEmitter.cs:311-340`), the restamp-operation
column lists (`PostgresqlGeneratedDdlAuthoritativeSmokeTests:277`, `MssqlGeneratedDdlAuthoritativeSmokeTests:211`,
`PostgresqlRepresentationRestampStore.cs`, `MssqlRepresentationRestampStore.cs`,
`RepresentationRestampContracts.cs`), CDC journal code (`CdcEstablishedValidation.cs:842`,
`CdcWorkflowJournal.cs`, `CdcWorkflowJournalValidation.cs`), `JwtValidationService`, `ValidationCache`,
and all of `src/config`.

### 5.5 Generated artifacts (regenerate)

| Artifact | Count | Regeneration |
|---|---|---|
| `**/expected/pgsql.sql` carrying `"CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),` inside `dms."Document"` | 18 (11 under `Backend.Ddl.Tests.Unit/Fixtures/{small,focused}`, 4 under `Backend.IntegrationFixtures`, 3 under `backend/Fixtures/authoritative/{ds-5.2,ds-5.2-tpdm,sample}`) | `UPDATE_GOLDENS=1` run of `Backend.Ddl.Tests.Unit` |
| `**/expected/mssql.sql` carrying `[CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Document_CreatedAt] …` | 18 (same set) | same |
| `**/expected/ddl.manifest.json` (17, alongside the SQL goldens; `no-ddl-manifest` has none) and `Backend.Ddl.Tests.Unit/Fixtures/ddl-emission/expected/ddl-manifest/*.json` (7) | 24 files, exactly the two `normalized_sql_sha256` lines each (one per dialect; a hash of the emitted DDL, so it changes with the SQL); `statement_count` unchanged | same `UPDATE_GOLDENS=1` run (corrected in step 1.1: originally listed as not affected) |
| `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/Fixtures/expected/provisioned-schema.{pgsql,mssql}.manifest.json` | 2 (`dms.Document.CreatedAt`, ordinal 7 — last, so no renumbering) | `UPDATE_GOLDENS=1` against live PostgreSQL and SQL Server |

**Not affected (verified):** `relational-model.{pgsql,mssql}.manifest.json` (every `CreatedAt`
entry is a tracked-change `role`), `authoritative-derived-relational-model-set.json` and
`focused-stable-key-derived-relational-model-set.json` (same),
`effective-schema.manifest.json` (hash inputs exclude DDL), `Fixtures/cdc/**`,
`Fixtures/document-cache/**`, `Fixtures/synthetic/**`.

### 5.6 Documentation

`reference/design/backend-redesign/design-docs/data-model.md:93, 122` (both DDL blocks),
`docs/RELATIONAL-BACKEND.md:146-186` (reprovisioning/carry-forward; line 177 "The tool currently
requires both document stamp columns"), `eng/northridge/README.md:141-143`,
`docs/changelog/8.1.0.md` (storage section near line 302), `reference/design/backend-redesign/epics/21-storage-reduction/EPIC.md:135-136` (story index).
`docs/` has no other mention of the column. The CDC operations runbook and the CDC design doc
are **not** edited; §7.4 cites them.

### 5.7 Unrelated same-named columns (retained, listed so reviewers can check the diff)

`tracked_changes_<schema>.<Resource>.CreatedAt` and `tracked_changes_edfi.Descriptor.CreatedAt`
(`TrackedChangeSystemColumnRole.CreatedAt`, `RelationalModelDdlEmitter.cs:311-340`),
`dms.RepresentationRestampOperation.CreatedAt` (`DmsCoreTableDefinitions.cs:140-146`),
`dmscs.Job/JobSchedule/Application/OpenIddictKey/ownership-token tables` (CMS), `setup-openiddict.ps1`,
CDC workflow journal `CreatedAt`.

---

## 6. Phases and steps

Conventions for every step: one local commit, explicit `git add <paths>` (never `-A`; never the
frontend `packages.lock.json`), message prefixed `[DMS-1403]`, CSharpier on changed `.cs` files
(§8.3 F1), **every commit compiles** (`dotnet build` of every project it touches and of the
provider test projects that consume the touched shared records), then stop and report SHA,
per-file summary, commands run with per-fixture pass/fail/skip counts by exact name (§8.2), PACs
touched, risks, next step. Silence is not approval. Evidence (`.trx`, console logs, diff stats)
is saved under `C:\dev\ed-fi\reviews\DMS-1403\<step>\` (outside the repository). Each step's
live-fixture filter is the one printed in §8.4 for that step.

### Phase 0 — Approval, bookkeeping, baseline (no product code)

**0.1 Spec challenge and approval.** Codex/user review of this document. Exit: explicit approval,
Q1 answered "no", any re-sequencing applied to the spec first.

**0.2 Commit the spec and the epic link.** Files: this spec, `EPIC.md` (one DMS-1403 line in the
story index, matching docs 15/16). Draft the Jira "Implementation Decisions" text (chosen option,
Q1–Q5 answers, PAC list, release handoff and CDC limitation from §7.5) in the step report for the
user to post; **no Jira write by this workflow**. Verification: markdown renders. Commit:
`[DMS-1403] Add implementation spec for dropping dms.Document.CreatedAt`.

**0.3 Baseline capture on the unchanged product tree.** The baseline commit is `205d5e98b`
(`origin/main` on 2026-10-12), recorded in `baseline\commit.txt` as its own value; it is **not**
`HEAD`, because 0.2 has already added the documentation commit on top of it. No product file
differs between the two, which `git diff --stat 205d5e98b HEAD -- src eng docs` must confirm
(expected: only `reference/**` and `EPIC.md`). Run U4 (all DMS unit projects), P4 (bootstrap
Pester lane subset), and I4 (MSSQL shards, environment permitting) exactly as in §8 and save
the results under `C:\dev\ed-fi\reviews\DMS-1403\baseline\`. Failing test names from this run
are the **only** tests that may later be reported as pre-existing. Run the §8.2 discovery
pre-check for every §8.4 list **restricted to fixtures that exist at the baseline** (every name
except the four marked *new in this story* in §8.4: the two 1.1 unit fixtures and the two 3.2 live
fixtures); a baseline name that does not discover stops the plan until the list is corrected. The
four new fixtures are instead discovered (`--list-tests`) and shown passing by exact name in
their own implementation step, before that step's commit. No commit in 0.3.

**Phase 0 acceptance:** approval recorded; spec committed; `baseline\commit.txt` = `205d5e98b`
and the product-tree diff against `HEAD` is empty; baseline files exist with failing names
extracted to `baseline\failing-names.txt`; every pre-existing §8.4 fixture name discovered.

### Phase 1 — Core definition, its expectations, emitted DDL (one commit)

**1.1 Remove the column and update everything derived from it inside `Backend.Ddl` /
`Backend.Ddl.Tests.Unit` / `Backend.External`.**
- Purpose: the single source of truth for `dms.Document` stops emitting `CreatedAt`; the commit is
  self-consistent (TR-1, TR-2, TR-12, TR-13).
- Files:
  - `DmsCoreTableDefinitions.cs` (delete lines 51-57; no reordering; `RepresentationRestampOperation` untouched).
  - `DocumentCacheInventoryDefinition.cs` (delete line 39).
  - `CoreDdlEmitterTests.cs`: replace `It_should_have_named_default_for_document_created_at` with
    `It_should_not_emit_document_created_at`, asserting that the `[dms].[Document]` CREATE block
    contains neither `[CreatedAt]` nor `[DF_Document_CreatedAt]`, and that the `"dms"."Document"`
    CREATE block contains no `"CreatedAt"` (both fail on revert); keep the remaining column/default
    assertions unchanged.
  - `CdcSourceInventoryTests.cs:327-347`: six names, `Enumerable.Range(1, 6)`; **add** fixture
    `Given_A_Legacy_Document_Inventory_With_CreatedAt` (named
    `Given_A_Legacy_Seven_Column_Document_Inventory` before the round 2 review; §0.5) with
    `It_reports_created_at_as_unexpected`, calling
    `CdcSourceInventoryValidator.ValidateLiveSourceInventory(expected, observedWithCreatedAtAtOrdinal7)`
    and asserting exactly one diagnostic `CDC_SOURCE_COLUMN_UNEXPECTED` for
    `dms.Document.CreatedAt`, `Classification == FailClosed`, and no `ORDER_MISMATCH` for the six
    surviving columns (fails on revert: the legacy inventory would then equal the expected one).
  - `CdcSqlServerHeartbeatDatabaseProviderTests.cs:363`: six-name `@captured_column_list`; **add**
    fixture `Given_An_Existing_Seven_Column_Document_Capture_Instance` in which the fake executor
    returns a **matching six-column source inventory** (so `ExecuteSourceInventoryAsync` adds no
    diagnostic and execution reaches `ExecuteCaptureInstancesAsync`) and an existing
    `dms_binding_document` capture instance whose captured columns are the seven legacy names;
    assert the capture-instance artifact is `Mismatched` with observed seven / expected six names
    in its observed values and the setup `Outcome == Failed`. Reuse the file's existing
    captured-column fixture rows and the mismatch assertions already present for other
    mismatch kinds; extend rather than duplicate.
  - The 36 `expected/{pgsql,mssql}.sql` goldens and the 24 DDL manifest JSONs (§5.5; only their
    `normalized_sql_sha256` lines) regenerated in the same commit.
- Commands (pwsh, repo root; `$R` from §8.1):
  ```powershell
  $env:UPDATE_GOLDENS = '1'
  dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit --nologo
  Remove-Item Env:\UPDATE_GOLDENS
  dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit --nologo --results-directory "$R\1.1" --logger "trx;LogFileName=U1.trx"
  dotnet test src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit --nologo --results-directory "$R\1.1" --logger "trx;LogFileName=U2.trx"
  dotnet build src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration --nologo
  Get-TrxFixtureCounts -TrxPath "$R\1.1\U1.trx" -ExpectedFixtures @(
    'EdFi.DataManagementService.Backend.Ddl.Tests.Unit.Given_A_Legacy_Document_Inventory_With_CreatedAt',
    'EdFi.DataManagementService.Backend.Ddl.Tests.Unit.Given_An_Existing_Seven_Column_Document_Capture_Instance')
  ```
  (The exact namespace of the two new fixtures is whatever the files already use; the list is
  corrected to the discovered name before the step report.)
- Diff-shape gate on the goldens (must hold before commit): `git diff --numstat -- '**/expected/*.sql'`
  shows exactly 36 files, each `0 added / 1 deleted`;
  `git diff -U0 -- '**/expected/*.sql' | Select-String '^-' | Where-Object { $_ -notmatch '^---' -and $_ -notmatch 'CreatedAt' }`
  is empty. For the manifests, `git diff --numstat -- '*.json'` (excluding the preserved frontend
  lockfile) shows exactly 24 files, each `2 added / 2 deleted`, and every changed line matches
  `^[-+]\s+"normalized_sql_sha256": "[0-9a-f]{64}",$`. Any other hunk is unrelated drift and
  stops the step.
- Dependencies: 0.1, 0.3.
- Risks: none to compilation (the `Backend.External` constant has no consumer; the CDC integration
  fixture still compiles because its column list is hand-written and is corrected in 3.1).
  Provider integration tests that SELECT `CreatedAt` now fail at runtime until Phase 2; they are
  not run in this step.
- Verification: U1 and U2 green (trx attached); both new fixtures executed by name; diff-shape
  gate output attached; `SchemaHashConstants.cs` unchanged.
- Commit: `[DMS-1403] Remove CreatedAt from the dms.Document definition and regenerate DDL goldens`.

**Phase 1 acceptance:** U1, U2 green; both new CDC fixtures executed by name; diff-shape gate
satisfied; a reviewer can read the 36-line golden diff in one screen (plus 48 hash-only manifest
lines).

### Phase 2 — Shared test support and provider integration tests (one shared concern per commit, both engines together)

Each step compiles `Backend.Tests.Common`, `Backend.Tests.Unit`,
`Backend.Postgresql.Tests.Integration`, and `Backend.Mssql.Tests.Integration`, then runs the step's
fixtures on both engines with the §8.4 filter and checks the §8.4 expected list by exact name.

**2.1 Materialized-document seeder.**
- Files: `MaterializedDocumentFixtureSeeder.cs` (drop the CREATE column and the INSERT column /
  parameter at 189, 334, 343), `MaterializedDocumentFixtureSeederTests.cs:88, 146` (pinned INSERT
  text on both dialects, now without `CreatedAt`).
- Verification: U3 (`Given_MaterializedDocumentFixtureSeeder`,
  `Given_Compact_Descriptor_Fixture_Seeds(Postgresql)`, and `Given_Compact_Descriptor_Fixture_Seeds(Mssql)`
  executed by name); §8.4 list 2.1 on each engine — the four fixtures that are the
  seeder's only integration consumers (`rg -l MaterializedDocumentFixtureSeeder` over the
  provider test projects).
- Commit: `[DMS-1403] Drop CreatedAt from the materialized-document fixture seeder`.

**2.2 Profile-guarded no-op snapshot and its two provider suites.**
- Files: `ProfileGuardedNoOpTestSupport.cs:16, 72`, `ParityScenarioCatalog.Profile.cs:525, 540, 555`,
  `PostgresqlProfileGuardedNoOpTests.cs:69, 715-718, 819-822`, `MssqlProfileGuardedNoOpTests.cs:73, 496, 601`
  (SELECT column; delete the two `It_does_not_change_created_at` tests per engine; every other
  assertion unchanged). The `ParityScenarioCatalog.Profile.cs` edit removes the deleted
  `It_does_not_change_created_at` method from each of the three catalog rows' method lists, which
  the parity catalog fixtures resolve by reflection.
- Verification: §8.4 list 2.2 on both engines, including its parity catalog gates.
- Commit: `[DMS-1403] Drop CreatedAt from the profile-guarded no-op snapshots`.

**2.3 No-profile guarded no-op snapshot and its two provider suites.**
- Files: `NoProfileGuardedNoOpScenarios.cs:122-128, 185, 224` (positional member + doc comments;
  the `ConcurrentBumpContentLastModifiedAt` sentinel check stays exactly as is),
  `ParityScenarioCatalog.NoProfile.cs:437`, `PostgresqlRelationalWriteGuardedNoOpTests.cs:196, 394, 577, 591`,
  `MssqlRelationalWriteGuardedNoOpTests.cs:207, 453, 639, 653`.
- Verification: §8.4 list 2.3 on both engines, including its parity catalog gates.
- Commit: `[DMS-1403] Drop CreatedAt from the no-profile guarded no-op snapshots`.

**2.4 POST-as-update snapshot and its two provider suites.**
- Files: `NoProfilePostAsUpdateScenarios.cs:101-152, 310, 463`,
  `PostgresqlRelationalWritePostAsUpdateSmokeTests.cs` (10 sites), `MssqlRelationalWritePostAsUpdateSmokeTests.cs` (10 sites).
- Verification: §8.4 list 2.4 on both engines.
- Commit: `[DMS-1403] Drop CreatedAt from the POST-as-update snapshots`.

**2.5 Authorization write side-effect state and its two provider suites.**
- Files: `AuthorizationWriteSideEffectState.cs:14`, `PostgresqlRelationalQueryAuthorizationTests.cs:2315, 2331`,
  `MssqlRelationalQueryAuthorizationTests.cs:2820, 2836`.
- Verification: §8.4 list 2.5 on both engines.
- Commit: `[DMS-1403] Drop CreatedAt from the authorization write side-effect state`.

**2.6 Atomic-rollback snapshot and descriptor-read support.**
- Files: `NoProfileAtomicRollbackAssertions.cs:133` and its one consumer
  `PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs:4419, 4445`;
  `PostgresqlDescriptorReadTestSupport.cs:125` / `...SupportTests.cs:150` and
  `MssqlDescriptorReadTestSupport.cs:130` / `...SupportTests.cs:161` (SELECT column; delete the
  `documentRow["CreatedAt"].Should().NotBeNull()` line; the sibling `ContentLastModifiedAt` and
  `ContentVersion > 0` assertions stay).
- Verification: §8.4 list 2.6 on both engines.
- Commit: `[DMS-1403] Drop CreatedAt from the rollback snapshot and descriptor-read support`.

**2.7 Test-local mirror tables (T-B, Q2 = yes).**
- Files: the 9 PostgreSQL, 8 SQL Server, and 1 shared T-B files in §5.4. Remove the `"CreatedAt"`
  / `[CreatedAt]` column from each private `CREATE TABLE dms."Document"` and from the INSERT
  column lists in the cache tests. No assertion changes.
- Verification: §8.4 list 2.7 on both engines.
- Commit: `[DMS-1403] Align test-local Document mirror tables with the schema`.

**Phase 2 acceptance:** `Backend.Tests.Unit` green; every fixture in §8.4 lists 2.1–2.7 green on
both engines by exact name; diff review confirms no surviving assertion was loosened (PAC-3);
`git grep -n CreatedAt -- 'src/dms/backend/*Tests*'` returns only these hits:
- T-C hits.
- **Permanent exceptions**, scoped to these test cases and not to their files. Any other
  `CreatedAt` hit in the same files fails the gate.
  - E1: step 1.1's legacy source-inventory fixture `Given_A_Legacy_Document_Inventory_With_CreatedAt`
    (`Backend.Ddl.Tests.Unit/CdcSourceInventoryTests.cs`).
  - E2: step 1.1's legacy capture-instance fixture
    `Given_An_Existing_Seven_Column_Document_Capture_Instance`
    (`Backend.Ddl.Tests.Unit/CdcSqlServerHeartbeatDatabaseProviderTests.cs`).
  - E3: the absence assertions in `It_should_not_emit_document_created_at`
    (`Backend.Ddl.Tests.Unit/CoreDdlEmitterTests.cs`). They check that neither the column nor its
    named default is emitted.
- **Unfinished references**, which step 3.1 removes and which are not exceptions:
  `MssqlCdcProviderArtifactsTests` and the pinned-image fixture
  (`CdcConnectorTemplatePinnedImageFixture.cs`, `….MessageContract.cs`).

### Phase 3 — CDC expectations, CDC rejection evidence, catalog goldens

**3.1 CDC capture-column expectations and the connector pinned-image fixture.**
- Files: `MssqlCdcProviderArtifactsTests.cs:868-880`, `CdcSqlServerHeartbeatDatabaseSchemaToolsTests.cs:796-808`
  (six columns), `CdcConnectorTemplatePinnedImageFixture.cs:93-108` (remove the `CreatedAt`
  entry so the CREATE at 2212-2234 / 3050-3075 matches the inventory),
  `CdcConnectorTemplatePinnedImageFixture.MessageContract.cs:90, 101` (drop the dead
  `"CreatedAt"` arms).
- Dependencies: 1.1.
- Verification: §8.4 list 3.1 (I3 `Given_MssqlCdcProviderArtifacts`; I5
  `Given_MssqlCdcHeartbeatDatabase_Provider_Setup`, `Given_PostgresqlCdcHeartbeatPublication_Provider_Setup`);
  `dotnet build` of `Backend.Cdc.Tests.Integration`. The pinned-image smoke test
  (`Category=CdcConnectorTemplateSmoke`) is Docker-based. It runs only in the "Run Backend
  CDC Connector Template Smoke Tests" job, which runs on `workflow_dispatch` only, so ordinary
  PR and merge-queue CI skip it. The message-contract fixtures (`….MessageContract.cs`) are
  `DatabaseIntegration` and skip locally without the pinned images. Both are required
  qualification runs against the reviewed branch SHA before merge (§8.3 I7-smoke and
  I7-contract) and are reported as not run locally.
- Commit: `[DMS-1403] Align CDC capture-column expectations with the six-column Document table`.

**3.2 Live source-inventory rejection on both engines (PAC-10 a).**
- Purpose: prove, against real engines, that a database whose `dms.Document` still carries
  `CreatedAt` — with every other CDC prerequisite valid — is rejected by the new runtime's
  provider setup at the source-inventory step, and that the rejection is the existing
  `FailClosed` diagnostic. Capture-instance mismatch is **not** asserted here (it cannot be
  reached once the source-inventory step fails; it is covered by the 1.1 unit fixture).
- Method ("legacy-shape simulation"): new fixtures
  `Given_PostgresqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column` (in
  `PostgresqlCdcProviderArtifactsTests.cs`) and
  `Given_MssqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column` (in
  `MssqlCdcProviderArtifactsTests.cs`). Each provisions from the new DDL exactly as the existing
  fixture does (same prerequisites: `wal_level=logical` / agent + `sys.sp_cdc_enable_db`,
  heartbeat table, principals), then
  `ALTER TABLE dms."Document" ADD COLUMN "CreatedAt" timestamp with time zone NOT NULL DEFAULT now()`
  (PG) / `ALTER TABLE [dms].[Document] ADD [CreatedAt] datetime2(7) NOT NULL CONSTRAINT [DF_Document_CreatedAt] DEFAULT (sysutcdatetime())`
  (SQL Server), runs setup in `CdcProviderSetupMode.ValidateOnly` with the new expected
  inventory, and asserts `Outcome == Failed`, exactly one `CDC_SOURCE_COLUMN_UNEXPECTED`
  diagnostic whose safe name is `dms.Document.CreatedAt`, `Classification == FailClosed`, and
  that no `SqlServerCaptureInstance` artifact observation was produced in that run (proving the
  service stopped at the source-inventory step, as designed). `MssqlCdcProviderArtifactsTests`
  already has `RunSetupAsync(connection, new(false), CdcProviderSetupMode.ValidateOnly)` (line
  267); the PostgreSQL file has no `ValidateOnly` run today and adds the smallest equivalent
  helper locally.
- Files: the two live test files above only.
- Verification: §8.4 list 3.2 (I1 and I3), both new fixtures executed by name.
- Why this passes only with the change: on the unchanged tree the ALTER fails (column exists)
  and the expected inventory has seven columns, so the assertions cannot be satisfied.
- Commit: `[DMS-1403] Prove CDC source-inventory validation rejects a Document table that still carries CreatedAt`.

**3.3 SchemaTools provisioned-schema catalog goldens.**
- Purpose: live-catalog proof of PAC-1 on both engines.
- Files: `provisioned-schema.pgsql.manifest.json`, `provisioned-schema.mssql.manifest.json`.
- Commands (both admin strings set, §8.1):
  ```powershell
  $env:UPDATE_GOLDENS = '1'
  dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration --nologo --filter "FullyQualifiedName~Given_Provisioned_Pgsql_Database_When_Introspecting_Schema|FullyQualifiedName~Given_Provisioned_Mssql_Database_When_Introspecting_Schema"
  Remove-Item Env:\UPDATE_GOLDENS
  dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration --nologo --filter "FullyQualifiedName~Given_Provisioned_Pgsql_Database_When_Introspecting_Schema|FullyQualifiedName~Given_Provisioned_Mssql_Database_When_Introspecting_Schema" --results-directory "$R\3.3" --logger "trx;LogFileName=I5-goldens.trx"
  Get-TrxFixtureCounts -TrxPath "$R\3.3\I5-goldens.trx" -ExpectedFixtures @(
    'EdFi.DataManagementService.SchemaTools.Tests.Integration.Given_Provisioned_Pgsql_Database_When_Introspecting_Schema',
    'EdFi.DataManagementService.SchemaTools.Tests.Integration.Given_Provisioned_Mssql_Database_When_Introspecting_Schema')
  ```
- Diff-shape gate: each manifest loses exactly the one `dms` / `Document` / `CreatedAt` object
  (one object, ten lines including braces) and nothing else.
- Commit: `[DMS-1403] Regenerate provisioned-schema catalog goldens`.

**Phase 3 acceptance:** both SchemaTools golden fixtures executed and green; CDC inventory/capture
tests green on both engines; both 3.2 fixtures executed and green by name; catalog queries in
§7.3 return no row for `CreatedAt`; `git grep -n CreatedAt -- src/dms` returns only these hits:
- §5.7 / T-C hits.
- Permanent exceptions E1–E3 (Phase 2 acceptance), scoped to those test cases.
- The intentional legacy-shape text in the two 3.2 fixtures,
  `Given_PostgresqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column` and
  `Given_MssqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column` (E4 and E5, scoped the same way).

No unfinished references remain after step 3.1. A `CreatedAt` hit anywhere else in a file that
holds an exception fails the gate.

### Phase 4 — Northridge copy-forward

**4.1 Stamp-distribution query and database-free guards.**
- Files: `Copy-NorthridgeDataForward.ps1` (`Get-StampDistribution`: the `dms.Document` SELECT
  becomes `COUNT|min/max ContentVersion|min/max ContentLastModifiedAt`, matching the sampled
  tables' shape; comments at 1106-1108 and 1120-1121), `NorthridgeFailClosedGuards.Tests.ps1`
  (stamp rows `dms.Document|3|1|9|a|b`; target column rows without `CreatedAt`; the insert-plan
  test asserts `$plan.Insert` ends at `ContentLastModifiedAt`, `$plan.SourceOnly` is
  `IdentityVersion, IdentityLastModifiedAt, CreatedAt`, and a new `It` proves the
  `Get-StampDistribution` function text no longer references `"CreatedAt"` while still
  referencing both surviving stamps), `eng/northridge/README.md:141-143`.
- Minimal change: no other guard, sequence, FK, fingerprint, or descriptor logic changes.
- Dependencies: none on C# steps (PowerShell only); logically after Phase 1.
- Risks: LF files (`eol=lf`); edit with LF-preserving tooling and check `git diff --stat` shows
  only the intended lines.
- Verification: P1.
- Commit: `[DMS-1403] Stop reading dms.Document.CreatedAt in the Northridge copy-forward`.

**4.2 Live carry-forward from an older dump that still carries `CreatedAt`.**
- Files: `CompactDescriptorCarryForward.Tests.ps1`. In the legacy-source DDL derivation, re-insert
  `"CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),` into `dms."Document"` (as the
  older schema had it) while the target keeps the current fixture DDL; keep the seed's explicit
  `CreatedAt` values on the source; add assertions that the target catalog has no `CreatedAt`
  after the copy and that the copy output reports it as source-only. The existing exact
  `dms.Document` value comparison compares every target column by name, because the source row
  now has one more column than the target (`dms.ReferentialIdentity` keeps its whole-row
  comparison).
- Prerequisite: disposable PostgreSQL 16 container, `DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER=<name>`.
- Verification: P2; both `-ForEach` forms pass; counts reported. This suite is opt-in locally and
  not in the CI lane, so the local run is the evidence.
- Commit: `[DMS-1403] Prove carry-forward drops a source-only CreatedAt`.

**Phase 4 acceptance:** PAC-5 evidence (P1 + P2) attached; no weakening of fail-closed guards
(the existing `Should -Throw` cases are unchanged and pass).

### Phase 5 — Documentation, template verifier, coordination

**5.1 Design and operator docs.**
- Files: `data-model.md` (remove the column from both DDL blocks and any note about it),
  `docs/RELATIONAL-BACKEND.md` (a DMS-1403 paragraph in "Reprovisioning and legacy data
  carry-forward": the column is removed on fresh schemas; `v3` retained; hash equality and
  first-use validation cannot prove the column is gone; the catalog queries of §7.3 as the
  verification; existing databases must be reprovisioned for the saving to exist; CDC
  setup/validation fails closed on a shape mismatch; **established CDC deployments have no
  supported upgrade/cutover path to the new shape in this release — the CDC design defers
  physical-source replacement, and that coordination is a separate release item (§7.4 c)**;
  templates built before this change still carry the column and serve but do not deliver the
  saving, and the verifier now rejects them (§7.5); update line 177 to say the copy tool no longer
  reads `CreatedAt` and tolerates source dumps carrying it), `eng/northridge/README.md`,
  `docs/changelog/8.1.0.md` (entry under the storage section with the same reprovisioning
  warning as the DMS-1404 entry plus the CDC limitation), `EPIC.md` (if not done in 0.2).
- Verification: `rg -n "CreatedAt" docs reference/design/backend-redesign/design-docs/data-model.md eng/northridge/README.md`;
  every hit is classified in the evidence as exactly one of (i) an unrelated same-named column
  (§5.7, e.g. tracked-change and descriptor-history `CreatedAt`), (ii) an intentional explanation
  of the removal, (iii) a §7.3 catalog query, or (iv) a limitation statement (existing databases,
  older templates, established CDC deployments, copy-tool tolerance of older dumps). No hit may
  describe `dms.Document.CreatedAt` as a current column. `rg -n -i "retire|re-enable|cutover" docs/RELATIONAL-BACKEND.md docs/changelog/8.1.0.md`
  shows no procedure text for established CDC deployments: every hit is the limitation statement
  or a statement that a route is not a CDC path. Links checked by eye.
- The template paragraph in 5.1 states that older packages retain the column and require
  replacement; the sentence that the verifier rejects them is added by step 5.3, which
  introduces that check, so that no commit documents behavior it does not contain.
- Commit: `[DMS-1403] Document the dms.Document.CreatedAt removal, reprovisioning, and CDC limitation`.

**5.2 Coordination record with DMS-1401 and release (no files).** In the PR description and in
the drafted Jira text: both stories edit `DmsCoreTableDefinitions.Document`, the 36 DDL goldens,
the two catalog manifests, the seeder, the snapshot records, `Get-StampDistribution`, the
Northridge required-column check, CDC expectations, and the template verifier's `dms.Document`
assertion. The semantic conflicts are named (§7.6). The second to merge re-checks every PAC and
regenerates evidence (§7.6). The release handoff (§7.5) includes the established-CDC limitation
verbatim.

**5.3 Template restore verifier: fail-closed `dms.Document` column-shape assertion (mandatory).**
- Design:
  - New function `Assert-DocumentTableShape` in `eng/DatabaseTemplates/Template-Management.psm1`
    (added to the `Export-ModuleMember -Function` list at line 1599) with parameters
    `-DatabaseEngine`, `-ContainerName`, `-DatabaseName`, `-MssqlPassword`, `-ForbiddenColumns`
    (default `@('CreatedAt')`), `-RequiredColumns` (default `@('DocumentId', 'DocumentUuid')`),
    and `-QueryRunner` (scriptblock; default runs `docker exec … psql -tA` / `docker exec …
    sqlcmd -h -1 -W` exactly as the verifier's `Invoke-PsqlScalar` / `Invoke-SqlcmdScalar` do,
    and throws on non-zero exit).
  - Query: PostgreSQL `SELECT column_name FROM information_schema.columns WHERE table_schema = 'dms' AND table_name = 'Document' ORDER BY ordinal_position;`
    SQL Server `SET NOCOUNT ON; SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dms.Document') ORDER BY c.column_id;`.
  - Semantics (each throws with the database name and engine): zero rows → "dms.Document is
    missing"; any required column absent → "unexpected dms.Document shape"; any forbidden column
    present → "dms.Document still carries <column>"; runner failure → rethrown, never treated as
    "absent". Success writes one line listing the observed columns.
  - `verify-template-restore.ps1`: call it for `$SourceDatabaseName` **before**
    `Restore-TemplatePackage` (line 152 neighbourhood) and for `$VerificationDatabaseName`
    **before** the serveability probe (after the schema-set check, line 170 neighbourhood),
    unconditionally (not behind `-ExpectedModelManifestPath`). Both calls are plain statements
    under `$ErrorActionPreference = "Stop"`, so a throw aborts the script.
  - `ContentLastModifiedAt` is deliberately **not** in `-RequiredColumns`, so the function stays
    valid on the combined shape. (Corrected 2026-10-14: the earlier assumption that DMS-1401 would
    extend `-ForbiddenColumns` is wrong. PR #1356 ships its own unconditional catalog assertion;
    see §7.6 for how the two are reconciled.) This step neither imports nor waits for #1356.
- Tests: new `eng/DatabaseTemplates/tests/Template-DocumentShape.Tests.ps1`, registered in the
  `on-dms-pullrequest.yml` Pester lane `paths` array (lines 220-279) next to
  `Template-Management.Tests.ps1`. Function-level cases (a)–(e), each `-ForEach` over
  `postgresql` and `mssql`: (a) runner returns the six new columns → passes; (b) runner returns
  the seven legacy columns → throws `*still carries CreatedAt*` naming the database; (c) runner
  returns nothing → throws `*missing*`; (d) runner throws / default runner sees `$LASTEXITCODE -ne 0`
  → throws and the message is not the "absent" message; (e) default runner path:
  `InModuleScope Template-Management` with `Mock docker` that records its arguments and sets
  `$global:LASTEXITCODE`, asserting the engine-specific command line (`psql -tA` vs
  `sqlcmd -h -1 -W`) and that the mocked output is parsed into the column list.
  **Script-level case (f), the invocation-path proof:** run
  `& eng/DatabaseTemplates/verify-template-restore.ps1 -SourceDatabaseName src_db -PackageDirectory $TestDrive -DatabaseEngine <engine>`
  under Pester mocks (plain `Mock`, not `-ModuleName`, per the lane's single-process constraint)
  for `Assert-DocumentTableShape`, `Get-UserSchemaNames`, `Restore-TemplatePackage`, `docker`,
  `Get-CmsToken`, `Invoke-WebRequest`, and the CMS helpers, with the `Invoke-*Scalar` paths
  reached only through the mocked `docker`: (f1) the mock throws when `-DatabaseName` is
  `src_db` → the script throws and `Should -Invoke Restore-TemplatePackage -Times 0`; (f2) the
  mock throws when `-DatabaseName` is `template_restore_verification` → the script throws,
  `Restore-TemplatePackage` was invoked once, and `Should -Invoke Get-CmsToken -Times 0` (no API
  probe); (f3) the mock passes for both → the script reaches `Get-CmsToken` (invoked once) and
  the mocked probe returns 200 with a non-empty array so the script completes. Both engines.
- Non-goal restated: no package build, restore matrix, publication, or consumer pin changes. The
  verifier's real end-to-end run (restore + DMS/CMS probe) is exercised only by the template
  workflows (§7.5 handoff).
- Docs: add to the DMS-1403 template paragraph in `docs/RELATIONAL-BACKEND.md` that
  `verify-template-restore.ps1` now rejects a source or restored database whose `dms.Document`
  still carries the column (§7.5).
- Verification: P3; lane registration visible in the workflow diff.
- Commit: `[DMS-1403] Assert dms.Document shape in template restore verification`.

**Phase 5 acceptance:** docs reviewed for PAC-6; P3 green with case groups (a)–(f) executed on
both engines; PAC-11 evidence attached; release handoff text (§7.5) present in the PR draft.

### Phase 6 — Evidence (optional, Q4) and push readiness

**6.1 Isolated storage measurement (optional).** On each engine: provision two DDL-only
databases from the previous (`git show 205d5e98b:<path>`) and the new `pgsql.sql` / `mssql.sql`
for `Fixtures/small/minimal`, insert the same 1,000,000 synthetic `dms.Document` rows from the
same generator script (`generate_series` on PostgreSQL; a numbers CTE on SQL Server; identical
values; defaults for stamps), `VACUUM (ANALYZE)` / no rebuild respectively, then record:
PostgreSQL `pg_table_size('dms."Document"')` (heap incl. visibility/free-space maps, excludes
indexes) and `pg_total_relation_size`; SQL Server `SUM(in_row_data_page_count)*8 KB` and
`SUM(used_page_count)*8 KB` from `sys.dm_db_partition_stats` for `index_id IN (0,1)` (clustered
index only). Report each component before/after and the delta per row, labelled per engine; state
that this measures one table under synthetic load. No gate; the numbers go into the PR
description. Script lives under `C:\dev\ed-fi\reviews\DMS-1403\6.1\`, not the repo.

**6.2 Pre-push validation.** Run every row marked **local pre-push** in §8.3; F1 on changed files;
`git status` shows only the pre-existing lock-file modification unstaged; `git log
origin/main..HEAD` lists the step commits. Present the commit list, file summary, PAC evidence
table (per-fixture counts by name), unresolved limitations (including §7.4 c), and the CI gates
that will run only after push (§8.3), then wait for push approval. The report also lists the
three qualification runs (I7-smoke and two I7-contract runs) still owed against the pushed SHA;
each dispatch waits for its own approval.

---

## 7. Cross-engine behavior, compatibility, rollout

### 7.1 Engine parity

The change is one definition consumed by both dialect emitters; PostgreSQL loses an unnamed
`DEFAULT now()` column, SQL Server loses a column with a named default constraint
`DF_Document_CreatedAt` (dropping the column drops the constraint; no orphan). Column order of
the remaining five columns is unchanged on both (six before DMS-1401 integration, §7.6). SQL
Server CDC capture instances are enabled by the provider with an explicit `@captured_column_list`
derived from the inventory, so a database provisioned from the new DDL gets a five-column capture
instance; the pinned expectations change to match.

### 7.2 Compatibility: API CRUD versus CDC setup/validation (two different answers)

**API CRUD (DMS runtime serving requests):** no API surface, response field, ETag,
`_lastModifiedDate`, change-query value, or ownership decision reads the column; no OpenAPI or
E2E change. No DMS statement names the column, so the new runtime writes correctly into an old
database (column present with a default) and an old runtime writes correctly into a new database.
Startup validation (`ValidateStartupInstancesTask`) cannot distinguish the two shapes because the
hash is unchanged (§7.3). CDC status does not gate request routing (runbook, "Missing provenance
and source mismatch").

**CDC setup and validation (initial enable, `cdc-validate`, guarded start/restart/resume
pre-checks, all via `CdcProviderSetupOrchestration` → `ICdcProviderSetupService`):** these compare
the live catalog against the expected inventory derived from `DmsCoreTableDefinitions.Document`
and **fail closed on any difference**, through two **independent mechanisms that do not fire
together** because the setup runs its steps in order and stops at the first error diagnostic:

| Mechanism | Step order (SQL Server) | PostgreSQL | SQL Server |
|---|---|---|---|
| Source inventory | 5th step (`ExecuteSourceInventoryAsync`) | `CDC_SOURCE_COLUMN_UNEXPECTED` (old DB, new runtime) or `CDC_SOURCE_COLUMN_MISSING` (new DB, old runtime) for `dms.Document.CreatedAt`, `FailClosed`, `Outcome = Failed`; later steps do not run | same (live inventory read at `CdcSqlServerHeartbeatDatabaseProvider.cs:427-448`) |
| Capture-instance columns | 6th step (`ExecuteCaptureInstancesAsync`), reached only when the source inventory matched | n/a | an existing capture instance whose captured list differs from the expected five names fails `capturedColumnsMatch` (around line 3160) → artifact `Mismatched` → `CDC_PROVIDER_ARTIFACT_MISMATCH` |

With the new runtime and a new database both mechanisms match exactly. Evidence: unit tests for
both mechanisms (1.1) and live source-inventory tests (3.2). The validator, the step order, and
the first-error stop are not changed; §2.2 forbids it.

### 7.3 Existing databases, schema hash, catalog verification

`RelationalMappingVersion` stays `v3` (AGENTS.md cadence; DMS-1404 already holds `v3`). Generated
DDL is not a hash input, so `EffectiveSchemaHash` is unchanged and `ValidateStartupInstancesTask`
accepts a pre-change database. The saving therefore exists only in databases provisioned from
the new DDL. Documentation must say so, and the verification is a catalog query, never the hash.
A bare "expect zero rows" query is insufficient because it also returns zero rows when the table
is missing; use the two-part form:

```sql
-- PostgreSQL: first query must return >= 1 row (table exists); second must return 0 rows
SELECT column_name FROM information_schema.columns
WHERE table_schema = 'dms' AND table_name = 'Document' ORDER BY ordinal_position;
SELECT column_name FROM information_schema.columns
WHERE table_schema = 'dms' AND table_name = 'Document' AND column_name = 'CreatedAt';
-- SQL Server: same two-part check
SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dms.Document') ORDER BY c.column_id;
SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dms.Document') AND c.name = N'CreatedAt';
```

In-place `ALTER TABLE … DROP COLUMN` is deliberately not provided (no migration framework exists
for the DMS relational schema; DMS-1404 set the "reprovision" precedent). Operators who want the
saving on an existing **non-CDC** PostgreSQL store use the copy-forward route; SQL Server stores
reprovision and reload. For CDC-enabled stores see §7.4 (c).

### 7.4 CDC rollout: what is supported and what is not

The source inventory is derived from the definition, so after this change the runtime's expected
`dms.Document` inventory has five columns (with DMS-1401, §7.6). Three situations must be kept
apart:

**(a) Fresh provisioning with eligible initial CDC enablement — supported.** A database
provisioned from the new DDL, with no prior binding, passes the initial-enable workflow's
pre-capture checks (controller-proven new physical source; no unexpected canonical/cache/work
rows) and gets a five-column capture instance on SQL Server and a matching publication on
PostgreSQL. Consumers that deserialize `dms.Document` change events by name never see
`createdAt` from such a source; the repository's connector templates and fixtures do not
enumerate the column.

**(b) Non-CDC reprovisioning / copy-forward — supported.** The §7.3 routes (copy-forward on
PostgreSQL, reprovision-and-reload on SQL Server) realize the saving for stores that have no
CDC binding. They are not CDC procedures and must not be presented as feeding a CDC enablement:
the initial-enable workflow rejects a target with unexpected canonical data before capture setup
(`cdc-streaming.md`, initial workflow rules), so "copy data forward, then enable CDC" is not an
established path.

**(c) Existing established CDC deployments — unsupported in this release; disclosed, not worked
around.** With the new runtime, `cdc-validate` and every guarded start/restart/resume pre-check
on an established binding whose database still carries `CreatedAt` **fail closed** (§7.2,
source-inventory mechanism); the DMS API keeps serving. The CDC design ("V1 physical-source
replacement deferral") exposes no source-replacement command, performs no replacement-driven
restore/copy, CMS cutover, or new-generation capture setup, and excludes accepting an
operator-prepared replacement database. Retiring the generation (`cdc-retire`) removes artifacts
but does not by itself provide a path to re-enable against a reprovisioned database with
continuity, and the runbook's source-mismatch procedure records evidence without repair.
Therefore this story does **not** document any upgrade/cutover sequence for such deployments.
The limitation is stated in `RELATIONAL-BACKEND.md`, the changelog, the PR, and the release
handoff (§7.5) as an unresolved delivery item that needs separate release coordination (and, if
a path is wanted, the separate replacement design the CDC doc calls for). DMS-1403 does not
expand into implementing CDC replacement.

### 7.5 Templates, verifier, release handoff

Template packages are built by provisioning from current DDL, so any build after this merge
produces five-column `dms.Document` tables without further work. Packages built before it remain
loadable and serveable (no reader) but do not carry the reduction, and after step 5.3 the shared
verifier **rejects** them by catalog, in both the source database and the restored verification
database, independent of the `v3` hash.

**Release handoff (recorded in the PR description and in the drafted Jira text, owner: 8.1
template/release workflow owner):**
1. The final 8.1 Minimal and Populated packages for both engines must be built from a `main` SHA
   that contains this change and must pass `verify-template-restore.ps1` with the
   `Assert-DocumentTableShape` check present. DMS-1401's planned rebuild satisfies this only if
   its build SHA is after this merge; the handoff must not assume that. Whichever of DMS-1401 /
   DMS-1403 merges second confirms the build SHA covers both removals.
2. **Established CDC deployments cannot be moved onto the new `dms.Document` shape by any
   supported procedure in this release (§7.4 c).** Release notes must say so, and release
   coordination must decide whether 8.1 ships with that limitation or waits for a replacement
   design. Generation retirement alone does not resolve it.

Package production, publication, release-view promotion, consumer pins, and the CDC replacement
design remain outside this story.

### 7.6 Coordination with DMS-1401

Same files, independent runtime semantics. Neither story needs the other's runtime change, and
either may merge first.

**State on 2026-10-14 (corrected; the earlier text assumed no PR existed).** DMS-1401 is PR #1356
(head `171094676`). 117 of the 121 files this story changes (`205d5e98b..ce92a573a`) are also
changed by #1356 (`08626c6f0..171094676`). DMS-1401 keeps `CreatedAt` and drops
`ContentLastModifiedAt`; this story does the reverse. The combined `dms.Document` has five
columns: `DocumentId`, `DocumentUuid`, `ResourceKeyId`, `CreatedByOwnershipTokenId`,
`ContentVersion`.

Overlaps:
- `DmsCoreTableDefinitions.Document`, all 36 goldens and their DDL manifests, and the two catalog
  manifests.
- The seeder, every snapshot record, and the test-local mirror tables.
- The CDC inventory, capture expectations, and connector fixture.
- `Get-StampDistribution` and the Northridge required-column check.
- The template verifier's `dms.Document` assertion.
- `data-model.md` and `RELATIONAL-BACKEND.md`.

**Semantic conflicts that a clean textual merge would hide:**
1. **Northridge copy-forward.** On #1356 the tool requires `CreatedAt` on the target
   ("Carry-forward requires retained document metadata CreatedAt") and compares
   `MIN/MAX("CreatedAt")`. This story does not change that required-column line, so git takes
   #1356's version without a conflict, and the combined tool would throw on every target
   provisioned from combined DDL. On `dms.Document`, the combined tool requires only
   `ContentVersion` and reads neither removed column. It keeps #1356's checks on the
   authoritative timestamps in resource roots and descriptors.
2. **Template verifier.** #1356 adds an unconditional SQL catalog assertion
   (`Assert-AuthoritativeTimestamp.*.sql`, run by `Assert-TemplateSchema.ps1` on the source and
   restored databases). It forbids `dms.Document.ContentLastModifiedAt`, validates
   `ContentVersion` and the owner timestamps, and makes `-ExpectedModelManifestPath` mandatory.
   This story's step 5.3 adds `Assert-DocumentTableShape` for `CreatedAt`; DMS-1401 does not
   extend it. At integration, consolidating the two into one check is preferred. Two deliberate
   checks are acceptable if both run at the required points: the source before the restore, and
   the restored database before the API probe. This story's simpler check must not replace
   DMS-1401's owner-timestamp, `ContentVersion`, or manifest validations.
3. **CDC expectations.** Each story pins a different six-column `dms.Document` inventory. The
   combined tree needs five columns in every inventory, capture-column expectation, and
   connector fixture. The legacy-column rejection fixtures (E1–E5) use an otherwise valid
   five-column baseline.
4. **Stamp ownership.** DMS-1401 moves timestamp ownership to resource roots and descriptors,
   which the guarded no-op, POST-as-update, rollback, and Northridge suites assert on. This
   story's surviving `ContentLastModifiedAt` assertions are rewritten or removed to match. They
   are never just deleted to make the merge compile.

**After DMS-1401 lands (before or after this story), the second story re-checks every PAC and
regenerates its evidence against the combined tree:** U1–U3, the §8.4 lists on both engines, I5,
P1–P3, and the diff-shape gates. The diff-shape gates are measured against the integration base,
`main` with the first story merged. Compared with `205d5e98b`, the combined tree removes two
`dms.Document` columns, so each golden and manifest loses two lines or objects, not one. The
combined-tree verifier evidence must show rejection when either removed column is present,
including when only one of them remains, on both engines and in both the source and the restored
check. This is not a mechanical rebase. DMS-1401 inherits the same §7.4 (c) limitation.

---

## 8. Test matrix

### 8.1 Prerequisites and conventions

PostgreSQL 16 container started with `-c max_locks_per_transaction=256 -c wal_level=logical
-c max_replication_slots=10` (`POSTGRES_HOST_AUTH_METHOD=trust`); SQL Server 2025 container
`dms-mssql-integration-2025` with `MSSQL_AGENT_ENABLED=true`, tmpfs data directory, and the CI
memory flags. Environment (pwsh):

```powershell
$env:ConnectionStrings__DatabaseConnection = 'Host=127.0.0.1;Port=5432;Username=postgres;Database=postgres'
$env:ConnectionStrings__PostgresAdmin      = $env:ConnectionStrings__DatabaseConnection
$env:ConnectionStrings__MssqlAdmin         = 'Server=127.0.0.1,14333;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true'
$R = 'C:\dev\ed-fi\reviews\DMS-1403'   # evidence root; one sub-folder per step
. "$R\Get-TrxFixtureCounts.ps1"          # §8.2
$PG   = 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration'
$MS   = 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration'
$PGNS = 'EdFi.DataManagementService.Backend.Postgresql.Tests.Integration'
$MSNS = 'EdFi.DataManagementService.Backend.Mssql.Tests.Integration'
```

Use `127.0.0.1`, never `localhost`, for IPv4-only published ports. Every `dotnet test` row passes
`--results-directory "$R\<step>" --logger "trx;LogFileName=<row>.trx" --logger console --nologo`.
Skipped tests never count as passing.

### 8.2 Per-fixture executed-count check (exact names)

Saved as `C:\dev\ed-fi\reviews\DMS-1403\Get-TrxFixtureCounts.ps1` (not in the repo). A step
passes only if **every** name in its `-ExpectedFixtures` list is present in the `.trx` with
`Passed > 0` and `Failed = 0`, matched by **exact** `className`; the function throws otherwise
and prints the full per-fixture table either way.

```powershell
function Get-TrxFixtureCounts {
    param([Parameter(Mandatory)][string]$TrxPath, [Parameter(Mandatory)][string[]]$ExpectedFixtures)
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $classById = @{}
    foreach ($u in Select-Xml -Xml $trx -Namespace $ns -XPath '//t:UnitTest') { $classById[$u.Node.id] = $u.Node.TestMethod.className }
    $rows = foreach ($r in Select-Xml -Xml $trx -Namespace $ns -XPath '//t:UnitTestResult') {
        [pscustomobject]@{ Fixture = $classById[$r.Node.testId]; Outcome = $r.Node.outcome }
    }
    $summary = $rows | Group-Object Fixture | ForEach-Object {
        [pscustomobject]@{
            Fixture = $_.Name
            Passed  = @($_.Group | Where-Object Outcome -eq 'Passed').Count
            Failed  = @($_.Group | Where-Object Outcome -eq 'Failed').Count
            Skipped = @($_.Group | Where-Object Outcome -eq 'NotExecuted').Count
        }
    } | Sort-Object Fixture
    $summary | Format-Table -AutoSize | Out-String | Write-Host
    $problems = foreach ($name in $ExpectedFixtures) {
        $hit = $summary | Where-Object Fixture -ceq $name
        if (-not $hit)              { "$name : not present in $TrxPath" }
        elseif ($hit.Passed -lt 1)  { "$name : no passing results (Passed=$($hit.Passed) Skipped=$($hit.Skipped))" }
        elseif ($hit.Failed -gt 0)  { "$name : Failed=$($hit.Failed)" }
    }
    if ($problems) { throw ("Expected-fixture check failed:`n" + ($problems -join "`n")) }
    $summary
}
```

**Discovery pre-check (run in 0.3 and whenever a list changes):** for each step list,
`dotnet test <project> --list-tests --filter "<step filter>"` must print at least one test for
every expected fixture (`Select-String -SimpleMatch "<ClassName>."`); a name that does not
discover stops the step until the list is corrected.

**Parameterized fixtures (corrected after the 0.3 baseline review).** A fixture class declared with
`[TestFixture(<argument>)]` is reported as `<ClassName>(<argument>)` in both the `--list-tests`
output (with `-- NUnit.DisplayName=FullName`) and the `.trx` `className`, never as the bare class
name. Each argument instance is therefore its own expected name, listed and checked
independently, and the discovery match `"<Name>."` applies to that full name. The only such
fixture in the §8.4 lists is `Given_Compact_Descriptor_Fixture_Seeds`, expected as
`…(Postgresql)` and `…(Mssql)` (list 2.1).

### 8.3 Matrix

Columns: **Local** = run locally before the step commit / pre-push; **CI** = required green on
the PR before the story is complete (not reproducible locally or deliberately CI-only).

| # | Suite | Command (pwsh, repo root; §8.1 variables) | Required fixtures / assertions | Local | CI |
|---|---|---|---|---|---|
| U1 | Ddl unit + goldens | `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit --results-directory "$R\<step>" --logger "trx;LogFileName=U1.trx" --nologo` | all pass; after 1.1 no `UPDATE_GOLDENS` needed; §8.4 list 1.1 by name | 1.1, 3.1, pre-push | Run Unit Tests |
| U2 | RelationalModel unit | `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit --results-directory "$R\<step>" --logger "trx;LogFileName=U2.trx" --nologo` | all pass, no golden changes | 1.1, pre-push | same |
| U3 | Backend unit (seeder consumers) | `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --filter "FullyQualifiedName~Given_MaterializedDocumentFixtureSeeder|FullyQualifiedName~Given_Compact_Descriptor_Fixture_Seeds" --results-directory "$R\<step>" --logger "trx;LogFileName=U3.trx" --nologo` (full project at pre-push) | `EdFi.DataManagementService.Backend.Tests.Unit.Given_MaterializedDocumentFixtureSeeder`, `…Given_Compact_Descriptor_Fixture_Seeds(Postgresql)`, and `…Given_Compact_Descriptor_Fixture_Seeds(Mssql)` by name, each independently | 2.1, pre-push | same |
| U4 | All DMS unit projects | `./build-dms.ps1 UnitTest` with output redirected to `"$R\<step>\U4.log"` | no failing names beyond `baseline\failing-names.txt` (0.3) | 0.3 (baseline), pre-push | same |
| I1 | PG backend integration, step fixtures | `dotnet test $PG --filter "<§8.4 PG filter for the step>" --results-directory "$R\<step>" --logger "trx;LogFileName=I1.trx" --nologo; Get-TrxFixtureCounts "$R\<step>\I1.trx" -ExpectedFixtures <§8.4 PG list>` | every listed fixture `Passed > 0`, `Failed = 0` | 2.1–2.7, 3.2 | PostgreSQL Integration lane |
| I2 | PG backend integration, full | `dotnet test $PG --results-directory "$R\6.2" --logger "trx;LogFileName=I2.trx" --nologo` | healthy baseline ≈1144 passed / 0 failed (≈27 min); any failure classified against 0.3 | pre-push | same |
| I3 | MSSQL backend integration, step fixtures | `dotnet test $MS --filter "<§8.4 MSSQL filter for the step>" --results-directory "$R\<step>" --logger "trx;LogFileName=I3.trx" --nologo; Get-TrxFixtureCounts "$R\<step>\I3.trx" -ExpectedFixtures <§8.4 MSSQL list>` | every listed fixture `Passed > 0`, `Failed = 0` | 2.1–2.7, 3.1, 3.2 | MSSQL shards 1–4 |
| I4 | MSSQL backend integration, full by CI shard | `foreach ($n in 1..4) { dotnet test $MS --filter "Category=MssqlCiShard$n" --results-directory "$R\<step>" --logger "trx;LogFileName=I4-shard$n.trx" --nologo }` | all pass per shard; the alias-mutex timeouts against a `127.0.0.1`-only binding count as pre-existing only if present in 0.3 | 0.3 (baseline), pre-push | MSSQL shards 1–4 |
| I5 | SchemaTools goldens + CDC heartbeat | `dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration --filter "FullyQualifiedName~Given_Provisioned_Pgsql_Database_When_Introspecting_Schema|FullyQualifiedName~Given_Provisioned_Mssql_Database_When_Introspecting_Schema|FullyQualifiedName~Given_MssqlCdcHeartbeatDatabase_Provider_Setup|FullyQualifiedName~Given_PostgresqlCdcHeartbeatPublication_Provider_Setup" --results-directory "$R\<step>" --logger "trx;LogFileName=I5.trx" --nologo; Get-TrxFixtureCounts … -ExpectedFixtures <§8.4 list 3.1/3.3 SchemaTools>` | all four fixtures by name; pass after 3.3 | 3.1, 3.3 | SchemaTools PG + MSSQL lanes |
| I6 | SchemaTools full PG and MSSQL | same project, `--filter "Category=PostgresqlIntegration"` then `--filter "Category=MssqlIntegration"`, `LogFileName=I6-pg.trx` / `I6-mssql.trx` | all pass | pre-push | same |
| I7 | Backend CDC integration, database lanes | `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration --filter "Category!=CdcConnectorTemplateSmoke" --results-directory "$R\<step>" --logger "trx;LogFileName=I7.trx" --nologo` | no failure except those whose name **and** signature match the I7 record from the pre-step-3.1 tree (`3.1\I7-revert-check-outcomes.csv`: 50 Kafka qualification `Docker operation failed (image)`, 3 Windows journal-interruption). Kafka qualification prerequisites can fail `OneTimeSetUp` rather than skip. Not-executed tests are counted separately from failures and passes, with each reason named (§0.5). | 3.1, pre-push | **Run Backend CDC Integration Tests** runs `Category!=DatabaseIntegration` only |
| I7-smoke | Connector-template pinned-image smoke | Qualification run: dispatch `on-dms-pullrequest.yml` (`workflow_dispatch`) on the branch; job `run-backend-cdc-connector-template-smoke-tests` (`Category=CdcConnectorTemplateSmoke`, Docker Compose stack, repository image variables) | pass on the reviewed branch SHA | not run locally (stated; 200 tests skip without the pinned-image variables) | required before merge; ordinary PR and merge-queue CI skip this job |
| I7-contract | CDC message contract, both providers | Qualification runs: dispatch `nightly-cdc-qualification.yml` twice on the branch, with `lane=Postgresql, suite=MessageContract` and `lane=Mssql, suite=MessageContract` | both pass on the reviewed branch SHA | not run locally (stated; 964 tests skip without `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE`) | required before merge; not part of PR CI |
| I8 | API integration (in-process DMS on provisioned DBs) | `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration -p:BuildingPluginFixtures=true --filter "Category=ApiIntegration&Category=PostgresqlIntegration" --results-directory "$R\6.2" --logger "trx;LogFileName=I8-pg.trx" --nologo` (+ `MssqlIntegration` variant when the MSSQL container is up) | pass; proves DMS boots and serves on a five-column `dms.Document` schema; plugin tests not run (NU1008 workaround) and stated | pre-push | PG + MSSQL API lanes, Plugin lane |
| P1 | Northridge db-free guards | `Invoke-Pester eng/northridge/tests/NorthridgeFailClosedGuards.Tests.ps1 -Output Detailed *> "$R\<step>\P1.log"` | all pass; the suite has no live scenarios; its skips are environment-dependent (no symbolic-link privilege; no POSIX `sh` on `PATH`, twice), named in the log, and must match the 0.3 baseline by name | 4.1, pre-push | Pester lane |
| P2 | Northridge live carry-forward | `$env:DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER='<pg16>'; Invoke-Pester eng/northridge/tests/CompactDescriptorCarryForward.Tests.ps1 -Output Detailed *> "$R\<step>\P2.log"`, run from Linux `pwsh` (on this Windows workstation, a disposable `mcr.microsoft.com/powershell` container with the Docker socket) | both `-ForEach` forms pass with nothing skipped; the source archive's `dms.Document` COPY header carries `CreatedAt`; the copy reports it as the only source-only column; the target catalog has no `CreatedAt`; the existing integrity assertions pass | 4.2, pre-push | not in CI (stated) |
| P3 | Template verifier Pester | `Invoke-Pester eng/DatabaseTemplates/tests -Output Detailed *> "$R\<step>\P3.log"` | all pass incl. `Template-DocumentShape.Tests.ps1` case groups (a)–(f) on both engines, named in the log | 5.3, pre-push | Pester lane |
| P4 | Bootstrap Pester lane subset | the `paths` array in `on-dms-pullrequest.yml` lines 220-279, each file via `Invoke-Pester <file> -Output Detailed`, output to `"$R\<step>\P4-<name>.log"` (AzureVmDeployment isolated per memory note) | no failing names beyond 0.3 baseline | 0.3 (baseline), pre-push | Pester lane |
| F1 | Format | `git diff --name-only --diff-filter=ACM origin/main -- '*.cs' \| ForEach-Object { dotnet csharpier check $_ }` | clean on every changed file (whole-tree drift is out of scope) | every C# step | Format lane |
| E1 | E2E | CI lanes (five E2E matrices) | green | not run locally unless I8 cannot run (then one shard after `Build -Configuration Release` and a teardown) | E2E lanes |

Criteria for the broader regressions: I2, I4, I6, and I8 are local pre-push gates because the
change alters the physical table every write path touches. E1 is a CI gate, reported as "not run
locally".

I7-smoke and I7-contract are required qualification runs, not PR CI. A green ordinary PR build
cannot satisfy them, because PR CI excludes `DatabaseIntegration` from the CDC integration
assembly and skips the smoke job. They are dispatched against the reviewed branch SHA after push
approval and before merge, and the §6.2 report records each run's URL and SHA. Dispatch is an
outward action and needs its own approval.

A story is complete only when both hold: the CI column is green on the PR, and all three
qualification runs are green on the reviewed SHA.

### 8.4 Expected-fixture lists and filters per step (exact class names, verified 2026-10-12)

Class names were listed from each file's `class Given_…` declarations. Namespaces:
`$PGNS` / `$MSNS` (§8.1). Filters are `FullyQualifiedName~<Class>` joined with `|`; the
expected list passed to `Get-TrxFixtureCounts` is `"$PGNS.<Class>"` / `"$MSNS.<Class>"` for each
name. Lists are declared once per step as PowerShell arrays saved under `$R\fixtures\<step>-pg.txt`
and `<step>-mssql.txt`; the filter is built as
`($list | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'` so the list and the filter cannot
diverge.

**1.1 (U1, Ddl.Tests.Unit)** — *new in this story:* `Given_A_Legacy_Document_Inventory_With_CreatedAt`,
`Given_An_Existing_Seven_Column_Document_Capture_Instance` (namespace as discovered; discovered
and shown passing in step 1.1, not at baseline), plus the existing fixture that owns
`It_should_not_emit_document_created_at` in `CoreDdlEmitterTests`.

**2.1 (seeder consumers — the only integration files referencing `MaterializedDocumentFixtureSeeder`,
verified by `rg -l`; corrected in review 3)**
- PG: `Given_Postgresql_DocumentCacheMaterializer_Fixtures` (`PostgresqlDocumentCacheMaterializerFixtureTests.cs`),
  `Given_Postgresql_DocumentCacheMaterializer_LinkBearingResource` (`PostgresqlDocumentCacheMaterializerLinkBearingResourceTests.cs`).
- MSSQL: `Given_Mssql_DocumentCacheMaterializer_Fixtures` (`MssqlDocumentCacheMaterializerFixtureTests.cs`),
  `Given_Mssql_DocumentCacheMaterializer_LinkBearingResource` (`MssqlDocumentCacheMaterializerLinkBearingResourceTests.cs`).
- Unit (U3, namespace `EdFi.DataManagementService.Backend.Tests.Unit`): `Given_MaterializedDocumentFixtureSeeder`,
  `Given_Compact_Descriptor_Fixture_Seeds(Postgresql)`, and `Given_Compact_Descriptor_Fixture_Seeds(Mssql)`
  (`CompactDescriptorFixtureSeedTests.cs`, a fixture parameterized by dialect; each instance is
  required independently, see §8.2; it references the seeder without naming `CreatedAt`).
  The mirror-table document-cache and hydration fixtures previously listed here do not use the
  seeder; they belong to 2.7 only.

**2.2 (profile-guarded no-op)**
- PG: `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape`,
  `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape`,
  `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape`,
  `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Shape`.
- MSSQL: `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape`,
  `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape`,
  `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape`,
  `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Shape`.
- Parity catalog gates (added after the step 2.2 review; they reflection-check the
  `ParityScenarioCatalog.*.cs` method lists and descriptions this step edits). Each is a separate
  row in its own project, with the same exact-name discovery and `Get-TrxFixtureCounts` check:
  - PG project: `EdFi.DataManagementService.Backend.Postgresql.Tests.Parity.Given_The_Postgresql_Parity_Catalog_Resolution`.
  - MSSQL project: `EdFi.DataManagementService.Backend.Mssql.Tests.Parity.Given_The_Mssql_Parity_Catalog_Resolution`.
  - Backend unit project (`Backend.Tests.Unit`): `EdFi.DataManagementService.Backend.Tests.Unit.Parity.Given_The_Parity_Scenario_Catalog`,
    `EdFi.DataManagementService.Backend.Tests.Unit.Parity.Given_The_Parity_Catalog_Entry_Point_Resolution`.

**2.3 (no-profile guarded no-op)**
- PG: `Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Guarded_No_Op_Put_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Competing_Uncommitted_Content_Version_Bump`,
  `Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Competing_Uncommitted_Content_Version_Bump`.
- MSSQL: the same six names with `Mssql` in place of `Postgresql`.
- Parity catalog gates (added after the step 2.2 review), each a separate row in its own project
  with exact-name discovery and the `Get-TrxFixtureCounts` check:
  - PG project: `EdFi.DataManagementService.Backend.Postgresql.Tests.Parity.Given_The_Postgresql_Parity_Catalog_Resolution`.
  - MSSQL project: `EdFi.DataManagementService.Backend.Mssql.Tests.Parity.Given_The_Mssql_Parity_Catalog_Resolution`.
  - Backend unit project (`Backend.Tests.Unit`): `EdFi.DataManagementService.Backend.Tests.Unit.Parity.Given_The_Parity_Scenario_Catalog`,
    `EdFi.DataManagementService.Backend.Tests.Unit.Parity.Given_The_Parity_Catalog_Entry_Point_Resolution`.

**2.4 (POST-as-update)**
- PG: `Given_A_Postgresql_Relational_Post_As_Update_Immutable_Identity_Change_With_A_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture`,
  `Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Post_Create_Race_With_The_Focused_Stable_Key_Fixture`,
  `Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Ds52_SchoolYearType_Fixture`,
  `Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture`.
- MSSQL: the same six names with `Mssql` in place of `Postgresql`.

**2.5 (query authorization)**
- PG: `Given_A_Postgresql_Relational_Query_Authorization_With_Direct_EdOrg_Claim_Match`,
  `Given_A_Postgresql_Relational_Query_Authorization_With_The_Authoritative_Ds52_School_Fixture`,
  `Given_A_Postgresql_Relational_Query_Authorization_With_A_Synthetic_EdOrg_Fixture`,
  `Given_A_Postgresql_Relational_Query_Authorization_With_A_Duplicate_People_Auth_Pair`,
  `Given_A_Postgresql_Relational_Query_Authorization_With_Anchored_Person_Pathways`.
- MSSQL: the same five names with `Mssql` in place of `Postgresql`.
- Side-effect reader consumers (added after the step 2.5 review). The fixtures above never reach
  the changed `ReadDocumentStateAsync` readers; these do, through the shared test context's
  `Read*SideEffectStateAsync`. Each is required by exact full name, with the same discovery and
  `Get-TrxFixtureCounts` check (`$R\fixtures\2.5-supp-pg.txt` / `2.5-supp-mssql.txt`):
  - PG: `$PGNS.Given_A_Postgresql_Descriptor_Write_With_Ownership_Authorization`,
    `$PGNS.Given_A_Postgresql_Post_With_Distinct_Create_And_Update_Authorization`,
    `$PGNS.Given_A_PostgresqlRelationalPutAuthorizationTests_With_A_Synthetic_EdOrg_Fixture`,
    `$PGNS.Given_A_PostgresqlRelationalPostAsUpdateAuthorizationTests_With_A_Synthetic_EdOrg_Fixture`,
    `$PGNS.Given_A_PostgresqlRelationalPutAuthorizationTests_With_A_Synthetic_People_Fixture`.
  - MSSQL: `$MSNS.Given_A_Mssql_Descriptor_Write_With_Ownership_Authorization`,
    `$MSNS.Given_A_Mssql_Relational_Namespace_Write_Authorization_With_A_Synthetic_Namespace_Fixture`,
    `$MSNS.Given_A_Mssql_Post_With_Distinct_Create_And_Update_Authorization`,
    `$MSNS.Given_A_MssqlRelationalPutAuthorizationTests_With_A_Synthetic_EdOrg_Fixture`,
    `$MSNS.Given_A_MssqlRelationalPutAuthorizationTests_With_A_Synthetic_People_Fixture`,
    `$MSNS.Given_A_MssqlRelationalPostAsUpdateAuthorizationTests_With_A_Synthetic_EdOrg_Fixture`.

**2.6 (rollback snapshot + descriptor read)**
- PG: `Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture`,
  `Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture`,
  `Given_A_Postgresql_Relational_Write_Key_Unification_Conflict_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture`,
  `Given_A_Postgresql_DescriptorRead_Test_Support`.
- MSSQL: `Given_A_Mssql_DescriptorRead_Test_Support`.
- Descriptor-read support consumers (added after the step 2.6 review). The changed
  `ReadDocumentRowAsync` helpers are also called by the GET-request fixtures. Each is required by
  exact full name in its own project, with the same discovery and `Get-TrxFixtureCounts` check
  (`$R\fixtures\2.6-supp-pg.txt` / `2.6-supp-mssql.txt`):
  - PG: `$PGNS.Given_A_Postgresql_DescriptorRead_Get_Request`.
  - MSSQL: `$MSNS.Given_A_Mssql_DescriptorRead_Get_Request`.

**2.7 (test-local mirror tables)**
- PG: `Given_A_Page_With_Multiple_Documents`, `Given_A_Postgresql_Selected_Page_Keyset_With_Nonascending_DocumentIds`,
  `Given_A_Single_DocumentId_Keyset`,
  `Given_HydrationExecutor_Single_Document_Fast_Path_With_DescriptorProjection_And_DocumentReferenceLookup`,
  `Given_A_Query_With_TotalCount_Requested`, `Given_A_Reference_Bearing_Resource`,
  `Given_Two_Postgresql_Hydration_Batches_On_The_Same_Transaction`,
  `Given_A_Pgsql_Query_Keyset_That_Returns_Its_Selected_Ids`,
  `Given_A_Postgresql_Page_With_Multi_Column_And_Child_Document_References`,
  `Given_A_Postgresql_Empty_Page_With_Document_References`, `Given_A_Postgresql_Descriptor_Only_Resource_Page`,
  `Given_Required_Descriptor_FK_Resolves_To_URI`, `Given_Null_Descriptor_FK_Omits_Property_From_Reconstituted_Document`,
  `Given_Page_With_Multiple_Documents_And_Distinct_Descriptors`,
  `Given_Descriptor_FK_Cleared_To_Null_Omits_Property_From_Reconstituted_Document`,
  `Given_Multi_Document_Page_Created_Via_Query_Keyset_Returns_All_Descriptor_URIs`,
  `Given_Key_Unified_Alias_Descriptor_FK_Executor_Returns_URI_From_Canonical_Column`,
  `Given_Key_Unified_Alias_Descriptor_FK_Full_Pipeline_Emits_URIs_At_Alias_Paths`,
  `Given_Postgresql_Reconstitution_With_Both_Descriptor_Fks_Set`,
  `Given_Postgresql_Reconstitution_With_Optional_Descriptor_Fk_Null`,
  `Given_Collection_Item_Descriptor_FK_Resolves_To_URI_Postgresql`,
  `Given_Collection_Item_Null_Descriptor_FK_Omits_Property_Postgresql`,
  `Given_Postgresql_DocumentCacheSourceMetadataReader`, `Given_Postgresql_DocumentCacheMaterializer_Descriptor`,
  `Given_Postgresql_DocumentCacheMaterializer_Coherence`.
- MSSQL: `Given_A_Page_With_Multiple_Documents_Mssql`, `Given_A_Single_DocumentId_Keyset_Mssql`,
  `Given_A_Query_With_TotalCount_Requested_Mssql`, `Given_A_Reference_Bearing_Resource_Mssql`,
  `Given_HydrationExecutor_Single_Document_Fast_Path_With_DescriptorProjection_And_DocumentReferenceLookup_Mssql`,
  `Given_A_Mssql_Query_Keyset_That_Returns_Its_Selected_Ids`,
  `Given_A_Mssql_Page_With_Multi_Column_And_Child_Document_References`,
  `Given_A_Mssql_Empty_Page_With_Document_References`, `Given_A_Mssql_Descriptor_Only_Resource_Page`,
  `Given_Required_Descriptor_FK_Resolves_To_URI_Mssql`,
  `Given_Null_Descriptor_FK_Omits_Property_From_Reconstituted_Document_Mssql`,
  `Given_Page_With_Multiple_Documents_And_Distinct_Descriptors_Mssql`,
  `Given_Descriptor_FK_Cleared_To_Null_Omits_Property_From_Reconstituted_Document_Mssql`,
  `Given_Multi_Document_Page_Created_Via_Query_Keyset_Returns_All_Descriptor_URIs_Mssql`,
  `Given_Mssql_Reconstitution_With_Both_Descriptor_Fks_Set`, `Given_Mssql_Reconstitution_With_Optional_Descriptor_Fk_Null`,
  `Given_Collection_Item_Descriptor_FK_Resolves_To_URI_Mssql`, `Given_Collection_Item_Null_Descriptor_FK_Omits_Property_Mssql`,
  `Given_Mssql_DocumentCacheSourceMetadataReader`, `Given_Mssql_DocumentCacheMaterializer_Descriptor`,
  `Given_Mssql_DocumentCacheMaterializer_Coherence`.

**3.1 (CDC pins)**
- MSSQL (I3): `Given_MssqlCdcProviderArtifacts`.
- SchemaTools (I5, namespace `EdFi.DataManagementService.SchemaTools.Tests.Integration`):
  `Given_MssqlCdcHeartbeatDatabase_Provider_Setup`, `Given_PostgresqlCdcHeartbeatPublication_Provider_Setup`.

**3.2 (live source-inventory rejection)**
- PG (I1): `Given_PostgresqlCdcProviderArtifacts`; *new in this story:* `Given_PostgresqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column`.
- MSSQL (I3): `Given_MssqlCdcProviderArtifacts`; *new in this story:* `Given_MssqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column`.
- The two new fixtures are discovered and shown passing in step 3.2 before its commit; they are
  excluded from the 0.3 baseline discovery.

**3.3 (SchemaTools goldens, I5):** `Given_Provisioned_Pgsql_Database_When_Introspecting_Schema`,
`Given_Provisioned_Mssql_Database_When_Introspecting_Schema`.

**5.3 (P3, Pester):** the `Describe`/`It` names of case groups (a)–(f) for both engines must
appear as passed in `P3.log` (Pester's `-Output Detailed` prints them); the step report lists
them.

---

## 9. Review checkpoints and revert-detectability

### 9.1 What each check establishes

| Check | Fails if only the production column removal is reverted? | What it establishes |
|---|---|---|
| `It_should_not_emit_document_created_at` (1.1) | yes | **physical removal** in emitted DDL |
| 36 generated DDL goldens (U1) | yes | **physical removal** in every fixture's DDL |
| Provisioned-schema catalog goldens (3.3) | yes | **physical removal** in live catalogs, both engines |
| Live legacy-shape source-inventory tests (3.2) | yes (ALTER fails; expected inventory differs) | **physical removal** reflected in the runtime inventory; rejection mechanism (a) |
| `CdcSourceInventoryTests` five-column pin + legacy-inventory diagnostic (1.1) | yes | derived inventory and mechanism (a) at unit level |
| `CdcSqlServerHeartbeatDatabaseProviderTests` five-name list + mismatch fixture (1.1) | yes (captured list) / component (mismatch fixture) | capture-list derivation; mechanism (b) at unit level |
| Seeder INSERT pins (2.1) | no — test-support component check | the shared seeder matches the new shape |
| `Get-StampDistribution` text assertion and `$plan.SourceOnly` pin (4.1) | no — tooling component check | the copy tool no longer reads the column |
| Live carry-forward (4.2) | only through the regenerated target DDL | tooling behavior against a legacy dump |
| `Template-DocumentShape.Tests.ps1` (a)–(f) (5.3) | no — verifier component check with mocked engines | the verifier rejects the old shape and wires the check into its flow |
| Behavior suites (PAC-3), full I2/I4/I6/I8, E2E | no | regression coverage; retained for that purpose |

### 9.2 Checkpoints

- After **every step**: stop after the local commit; report SHA, file-by-file purpose, commands
  run with per-fixture pass/fail/skip counts by exact name (§8.2), PACs addressed, remaining
  risks, proposed next step; wait for approve/reject. On reject: fix only the findings, commit
  the correction, re-present.
- **Phase-level acceptance** (listed at the end of each phase) must be explicitly accepted before
  the next phase starts.
- **Spec change control**: new evidence that changes scope or order (for example a reader of
  `CreatedAt` found in a plugin, or DMS-1401 landing first) updates this document first and is
  re-approved before implementation continues.
- **Push gate**: §6.2 report and explicit approval; never push on silence.

---

## 10. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Golden regeneration captures unrelated drift | low | diff-shape gates in 1.1 and 3.3 |
| DMS-1401 lands concurrently on the same files and on stamp semantics | high (PR #1356 open on 2026-10-14) | §7.6: four named semantic conflicts; re-check every PAC and regenerate evidence against the combined tree; not a mechanical rebase |
| A hidden reader in a plugin or external SQL (custom auth views) | low | PAC-2 grep across `src`, `eng`, `reference`; custom views reference `DocumentId`, not timestamps; changelog entry tells operators |
| Template built before the change ships as "8.1" and silently keeps the column | medium | 5.3 verifier assertion rejects it by catalog; §7.5 release handoff names the owner and the SHA requirement |
| Established CDC deployments cannot be upgraded to the new shape | certain for such deployments | disclosed as unsupported (§7.4 c, §7.5 item 2); no workaround documented; release coordination decides |
| A fixture name in §8.4 drifts or never discovers | low | 0.3 discovery pre-check; §8.2 fails on any missing name |
| Windows-only unit failures (CDC journal, SchemaTools) confuse the gate | known | 0.3 baseline by name; nothing is "pre-existing" without it |
| Live suites skip silently (no `wal_level=logical`, no agent, missing env var) | known | §8.2 per-fixture executed counts by name; §8.1 flags |
| LF damage to `.ps1` files from Windows tooling | known | LF-preserving edits; `git diff --stat` line counts |
| `Mock docker` / script-level mocks in the Pester lane's single process leak or collide | medium | plain `Mock` scoped to the `Describe` block, `InModuleScope` for module internals, no `Mock -ModuleName` (memory: Pester lane gotchas) |
