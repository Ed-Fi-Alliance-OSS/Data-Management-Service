# DMS-1129 Complete-Vector Feasibility Evidence

## Status

This is reproducible static evidence for Design Reset Gate 1. It measures the complete intrinsic propagation-vector
hypothesis against the checked-in authoritative Data Standard 5.2 and Data Standard 5.2 plus TPDM inputs.

The static result plus focused maximum-value provider probes pass Design Reset Gate 1's measured key-width/column-count
screen for choosing one complete vector per target. Full generated Data Standard/TPDM DDL, total SQL Server row width,
and PostgreSQL tuple/index overhead remain implementation qualification items; they are not evidence for retaining
site-minimal anchor closure in the design.

## Reproduce

From the repository root:

```bash
node reference/design/backend-redesign/evidence/dms-1129/measure-complete-vectors.js
```

The default mode recomputes the measurement and byte-compares it with
`complete-vector-feasibility-summary.json`. Use `--print` to inspect generated JSON or `--write` to intentionally
regenerate the checked-in summary.

The script reads the same authoritative ApiSchema inputs and relational-model manifests used by the DDL golden-fixture
pipeline. It does not modify those fixtures.

## Measurement Model

The prototype applies these rules:

1. One root identity-contributing document reference is one independently replaceable identity lineage, regardless of
   how many public identity values that reference supplies.
2. Descriptor references are excluded because descriptor identity is immutable and descriptor references are not
   document-reference lineage replacements.
3. Abstract-resource lineages are normalized across every concrete member. All members must supply the same referenced
   row meaning. This adds the three common `EducationOrganization`, `Program`, and `Student` anchors to
   `GeneralStudentProgramAssociationIdentity`; `EducationOrganizationIdentity` has no reference-backed lineage.
4. A target propagation vector is ordered as:

   ```text
   public RefKey storage columns,
   every intrinsic lineage DocumentId anchor,
   target DocumentId
   ```

5. Every incoming reference carries that same target vector. The conservative storage result allocates a dedicated
   `BIGINT` for every incoming site/lineage pair. A secondary result reuses an existing required direct-reference
   `DocumentId` only when every correlated canonical public storage column is identical.
6. SQL Server strings are measured as `nvarchar(n)` at two bytes per declared character. PostgreSQL reports both ASCII
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
| Maximum intrinsic anchors on one target | 3 | 3 |
| Maximum vector columns | 13 | 14 |
| Conservative added anchor columns | 152 | 176 |
| Added anchors after obvious safe reuse | 150 | 174 |
| Maximum added fixed row bytes on one table | 64 | 64 |
| Maximum final table column count | 44 | 48 |
| Added propagation UNIQUE constraints over one per target | 0 | 0 |

The widest column-count cases are:

- DS 5.2 `Grade`: 10 public identity columns + 2 lineage anchors + `DocumentId` = 13 columns.
- TPDM `EvaluationObjectiveRating`: 11 public identity columns + 2 lineage anchors + `DocumentId` = 14 columns.

The largest receiver-table increase is `StudentAssessmentRegistration`: eight dedicated anchors, 64 fixed bytes, and a
column-count increase from 25 to 33.

The only obvious safe storage reuses found in either corpus are the Session School lineage at
`CourseOffering.sessionReference` and `StudentSchoolAttendanceEvent.sessionReference`; both reuse the required local
`School_DocumentId`. The conservative feasibility result does not depend on those reuses.

## Measured Provider Screen

Neither corpus exceeds the 32-column key limit used by SQL Server foreign keys or a default PostgreSQL index, and no
table approaches SQL Server's 1,024-column limit. See the official
[SQL Server capacity limits](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server)
and [PostgreSQL limits](https://www.postgresql.org/docs/current/limits.html).

The maximum SQL Server declared vector payload is 1,284 bytes on `SurveySectionResponse`. It is below the 1,700-byte
nonclustered UNIQUE-key limit but above the capacity table's separate 900-byte foreign-key figure. Five targets exceed
that documented screening figure in declared bytes:

- `AssessmentAdministration`: 1,156 baseline, 1,172 complete;
- `Grade`: 973 baseline, 989 complete;
- `StudentAssessmentRegistration`: 1,228 baseline, 1,244 complete;
- `SurveySection`: 1,148 baseline, 1,156 complete; and
- `SurveySectionResponse`: 1,268 baseline, 1,284 complete.

All five already exceed 900 bytes before adding complete-vector anchors. Complete vectors introduce **zero** new
900-byte crossings. This is therefore an existing full-composite-v1 risk, not evidence that site-minimal anchor demand is
needed.

The largest PostgreSQL public-plus-anchor payload estimate is 654 ASCII bytes or 2,544 bytes with every character encoded
as four-byte UTF-8. That is below the prototype's 2,704-byte default-page screening threshold before tuple overhead.

This static screen does not calculate total SQL Server row width, actual physical index size, or PostgreSQL B-tree tuple
overhead/compression. Those remain full generated-model qualification work.

Focused probes exercised the worst `SurveySectionResponse` shape rather than a narrower surrogate:

- SQL Server 2022 CU20 installed the seven-column 1,284-byte target UNIQUE and full child FK, then accepted a target and
  child row with every `nvarchar` component at its declared maximum.
- PostgreSQL 18.4 installed the equivalent full FK and accepted target/child rows with a measured 2,544-byte payload
  using four-byte UTF-8 characters at every declared maximum.

The provider probes are checked in as
[`MssqlCompletePropagationVectorFeasibilityTests.cs`](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCompletePropagationVectorFeasibilityTests.cs)
and
[`PostgresqlCompletePropagationVectorFeasibilityTests.cs`](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCompletePropagationVectorFeasibilityTests.cs).
These results resolve the measured width risk; they do not replace the later full-schema generated-DDL qualification.

No measured case fails the complete inventory while a site-minimal anchor subset would fit a column-count or
nonclustered-UNIQUE limit.

## Artifact Projection

The script projects only generic table columns and expanded FK, target propagation-key, all-or-none, and FK-support-index
arrays. It deliberately adds no solver trace, stable semantic hash protocol, omission proof, certificate tree, or
generalized deferred-reference plan.

| Projected artifact growth | DS 5.2 | DS 5.2 + TPDM |
| --- | ---: | ---: |
| Relational-model manifest | 153,861 bytes (2.52%) | 184,863 bytes (2.66%) |
| SQL Server DDL, rough text projection | 48,520 bytes (0.81%) | 56,200 bytes (0.82%) |

Mapping-pack growth cannot yet be measured. `MappingPackPayload` is an empty placeholder and `MappingSet.FromPayload`
throws `NotSupportedException`; the repository has no generated `.mpack` artifact. This supports deferring mapping-pack
protocol changes until the runtime slice establishes the exact consumed fields.

## Remaining Implementation Qualification

Gate 1's measured screen is passed for the architecture choice. The implementation slices must still:

1. Implement the complete-vector prototype in relational-model derivation and emit real provider DDL.
2. Install the full DS 5.2 and DS 5.2 plus TPDM DDL on both providers.
3. Retain maximum-value cases for all five wide SQL Server vectors and the widest PostgreSQL vectors in generated-model
   provider coverage.
4. Measure actual row/index sizes and derivation time from the implemented model rather than this static payload estimate.
5. Measure mapping-pack size only after a real pack payload exists.

If a provider probe fails because of complete anchors, preserve the concrete failing fixture and introduce only the
smallest measured demand-reduction mechanism needed for that case. Do not retain the current generalized site-minimal
closure preemptively.
