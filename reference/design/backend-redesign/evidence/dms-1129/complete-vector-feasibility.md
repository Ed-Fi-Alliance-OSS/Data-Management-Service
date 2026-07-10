# DMS-1129 Complete-Vector Feasibility Evidence

## Status

This is reproducible static evidence for Design Reset Gate 1. It measures the complete transitive propagation-vector
hypothesis against the checked-in authoritative Data Standard 5.2 and Data Standard 5.2 plus TPDM inputs.

The static result plus focused maximum-value provider probes pass Design Reset Gate 1's measured key-width/column-count
screen for choosing one complete vector per target. Full generated Data Standard/TPDM DDL, the 27-column widest-count
case, total SQL Server row width, PostgreSQL tuple/index overhead, actual supporting-index size, reference-resolution
round trips, and write/cascade timing remain implementation qualification items; they are not evidence for retaining
site-minimal anchor closure in the design. DMS-1274 must pass an early representative physical row/index and
write-amplification gate before the `v2` storage shape is frozen; DMS-1277 retains exhaustive qualification.

## Reproduce

From the repository root:

```bash
node reference/design/backend-redesign/evidence/dms-1129/measure-complete-vectors.js
```

The default mode recomputes the measurement and byte-compares it with
`complete-vector-feasibility-summary.json`. Use `--print` to inspect generated JSON or `--write` to intentionally
regenerate the checked-in summary. Every mode exits nonzero when the computed
`anchor_caused_limit_crossings` inventory is non-empty.

The script reads the same authoritative ApiSchema inputs and relational-model manifests used by the DDL golden-fixture
pipeline. It does not modify those fixtures.

## Measurement Model

The prototype applies these rules:

1. One root identity-contributing document reference is one independently replaceable direct identity lineage,
   regardless of how many public identity values that reference supplies.
2. For each direct lineage `T -> U`, the complete inventory contains `U.DocumentId` followed by the complete lineage
   inventory of `U`. Recursive expansion follows stable structural reference order. This carries nested anchors needed
   when public values inherited through `U` are key-unified with another receiver reference.
3. Descriptor references are excluded because descriptor identity is immutable and descriptor references are not
   document-reference lineage replacements.
4. Abstract-resource lineages are normalized across every concrete member. All members must supply exactly the same
   referenced row meaning. This adds the three common `EducationOrganization`, `Program`, and `Student` anchors to
   `GeneralStudentProgramAssociationIdentity`, plus their transitive inventories; `EducationOrganizationIdentity` has no
   reference-backed lineage.
5. A target propagation vector is ordered as:

   ```text
   public RefKey storage columns,
   every complete transitive lineage DocumentId anchor,
   target DocumentId
   ```

6. Every incoming reference carries that same target vector. The conservative storage result allocates a `BIGINT` for
   every inherited target lineage, uses that same column on the identity site that supplies it, and allocates a dedicated
   `BIGINT` for every other incoming site/lineage pair. A secondary result reuses an existing required direct-reference
   `DocumentId` only when every correlated canonical public storage column is identical.
7. SQL Server strings are measured as `nvarchar(n)` at two bytes per declared character. PostgreSQL reports both ASCII
   and four-byte UTF-8 payload bounds. PostgreSQL B-tree tuple overhead and compression are not modeled.

This is deliberately not the removed minimal-demand algorithm. It computes no per-site anchor subset, omission proof,
or propagation-key variant.

## Results

| Measurement | DS 5.2 | DS 5.2 + TPDM |
| --- | ---: | ---: |
| Document-reference sites / full-composite FKs | 318 | 349 |
| Referenced targets / propagation UNIQUE constraints | 72 | 81 |
| Anchor-bearing target keys | 43 | 51 |
| Anchor-bearing incoming FKs | 113 | 129 |
| Maximum complete lineage anchors on one target | 11 | 15 |
| Maximum vector columns | 22 | 27 |
| Conservative added anchor columns | 238 | 308 |
| Added anchors after obvious safe reuse | 236 | 306 |
| Maximum added fixed row bytes on one table | 88 | 160 |
| Maximum final table column count | 44 | 65 |
| Added propagation UNIQUE constraints over one per target | 0 | 0 |

The widest column-count cases are:

- DS 5.2 `Grade`: 10 public identity columns + 11 complete lineage anchors + `DocumentId` = 22 columns.
- TPDM `EvaluationObjectiveRating`: 11 public identity columns + 15 complete lineage anchors + `DocumentId` = 27 columns.

The largest DS 5.2 table increase is `ReportCardGrade`: 11 anchors, 88 fixed bytes, and a column-count increase from 17 to
28. The largest TPDM table increase is `EvaluationElementRating`: 20 anchors, 160 fixed bytes, and a column-count increase
from 45 to 65. These conservative totals count inherited identity-site anchors once as target storage and add dedicated
storage only for other incoming sites.

The only obvious safe storage reuses found in either corpus are the Session School lineage at
`CourseOffering.sessionReference` and `StudentSchoolAttendanceEvent.sessionReference`; both reuse the required local
`School_DocumentId`. The conservative feasibility result does not depend on those reuses.

## Measured Provider Screen

Neither corpus exceeds the 32-column key limit used by SQL Server foreign keys or a default PostgreSQL index, and no
table approaches SQL Server's 1,024-column limit. See the official
[SQL Server capacity limits](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server)
and [PostgreSQL limits](https://www.postgresql.org/docs/current/limits.html).

The maximum SQL Server declared vector payload is 1,300 bytes on `SurveySectionResponse`. It is below the 1,700-byte
nonclustered UNIQUE-key limit but above the capacity table's separate 900-byte foreign-key figure. Five targets exceed
that documented screening figure in declared bytes:

- `AssessmentAdministration`: 1,156 baseline, 1,172 complete;
- `Grade`: 973 baseline, 1,061 complete;
- `StudentAssessmentRegistration`: 1,228 baseline, 1,276 complete;
- `SurveySection`: 1,148 baseline, 1,156 complete; and
- `SurveySectionResponse`: 1,268 baseline, 1,300 complete.

All five already exceed 900 bytes before adding complete-vector anchors. Complete vectors introduce **zero** new
900-byte crossings. This is therefore an existing full-composite-v1 risk, not evidence that site-minimal anchor demand is
needed.

The largest PostgreSQL public-plus-anchor payload estimate is 674 ASCII bytes or 2,560 bytes with every character encoded
as four-byte UTF-8. That is below the prototype's 2,704-byte default-page screening threshold before tuple overhead.

This static screen does not calculate total SQL Server row width, actual target-unique or FK-supporting index size,
PostgreSQL B-tree tuple overhead/compression, or physical storage at representative row counts. Those remain full
generated-model qualification work. DMS-1274 must run an early representative physical row/index and write-amplification
gate before the `v2` storage shape is treated as fixed; DMS-1277 expands that gate to exhaustive supported-schema and
adversarial qualification.

Focused probes exercised the widest-declared-byte `SurveySectionResponse` shape rather than a narrower surrogate:

- SQL Server 2022 (`16.0.4205.1`) installed the nine-column 1,300-byte target UNIQUE and full cascading child FK, accepted
  a target and child row with every `nvarchar` component at its declared maximum, and cascaded a maximum-width public
  value plus a lineage anchor.
- PostgreSQL 18.4 installed the equivalent cascading full FK, accepted target/child rows with a measured 2,560-byte
  payload using distinct, incompressible four-byte UTF-8 characters at every declared maximum, and cascaded a
  maximum-width public value plus a lineage anchor.

The probe's nine-column vector does not exercise the 27-column widest-count TPDM
`EvaluationObjectiveRating` vector. The provider probes are checked in as
[`MssqlCompletePropagationVectorFeasibilityTests.cs`](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCompletePropagationVectorFeasibilityTests.cs)
and
[`PostgresqlCompletePropagationVectorFeasibilityTests.cs`](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCompletePropagationVectorFeasibilityTests.cs).
These results resolve only the measured key-width/column-count architecture screen; they do not replace the later
widest-count or full-schema generated-DDL qualification.

The computed `anchor_caused_limit_crossings` inventory is empty: adding complete anchors causes no measured vector,
declared-key payload, or table-column threshold crossing. The tool makes no claim about whether a removed site-minimal
algorithm would fit a hypothetical failing case.

## Artifact Projection

The script projects only generic table columns and expanded FK, target propagation-key, all-or-none, and FK-support-index
arrays. It deliberately adds no solver trace, stable semantic hash protocol, omission proof, certificate tree, or
generalized deferred-reference plan.

| Projected artifact growth | DS 5.2 | DS 5.2 + TPDM |
| --- | ---: | ---: |
| Relational-model manifest | 198,507 bytes (3.25%) | 252,867 bytes (3.64%) |
| SQL Server DDL, rough text projection | 76,000 bytes (1.26%) | 98,400 bytes (1.43%) |

## Remaining Implementation Qualification

Gate 1's corrected transitive measured screen is passed for the architecture choice. The implementation slices must still:

1. Implement the complete-vector prototype in relational-model derivation and emit real provider DDL.
2. During DMS-1274, measure representative high-volume target and FK-supporting indexes, total row size, and write
   amplification before freezing the `v2` storage shape.
3. Install the full DS 5.2 and DS 5.2 plus TPDM DDL on both providers.
4. Add maximum-value provider coverage for the 27-column widest-count vector, all five wide SQL Server vectors, and the
   widest PostgreSQL vectors in the generated model.
5. Measure actual row and target-unique/FK-supporting index sizes at representative row counts rather than this static
   payload estimate.
6. Measure reference-resolution database commands/round trips, including requests with several distinct anchor-bearing
   target groups; prefer a single batched/multi-result command where supported.
7. Measure derivation time and representative write/cascade timing for stock and TPDM schemas.

If a provider probe fails because of complete anchors, preserve the concrete failing fixture and introduce only the
smallest measured demand-reduction mechanism needed for that case. Do not retain the current generalized site-minimal
closure preemptively.
