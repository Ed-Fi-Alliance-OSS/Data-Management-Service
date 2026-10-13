# DMS-1401 template qualification

[PostgreSQL Minimal evidence](minimal-postgresql.json) records T20's successful
[non-publishing run](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38343258018).
Both DS 5.2.0 core+TPDM and DS 6.1.0 core used fresh PostgreSQL 16.8 sources and
fresh restoration targets. Source and restored timestamp/compact-descriptor
catalog gates, schema/data checks, and authenticated descriptor reads passed.

[SQL Server Minimal evidence](minimal-mssql.json) records T21's successful
[non-publishing run](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38344308663).
Both Data Standard legs used fresh SQL Server 2025 sources and fresh backup
restoration targets. Source and restored timestamp/compact-descriptor catalog
gates, schema/data checks, and authenticated descriptor reads passed. DS 5.2.0
restored 3,350 documents and 3,303 descriptors; DS 6.1.0 restored 4,035 documents
and 3,988 descriptors. Both SQL Server manifests match the reviewed T18 manifests
byte for byte; downloaded package hashes match verification and provenance.

[PostgreSQL Populated evidence](populated-postgresql.json) records T22's successful
[non-publishing run](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38345459901).
Both Data Standard legs used fresh PostgreSQL 16.8 sources and restoration targets
with `RequirePopulatedData=true`. Source and restored timestamp/compact-descriptor
catalog gates passed before authenticated API probes. DS 5.2.0 restored 101,424
populated documents and DS 6.1.0 restored 100,495, excluding descriptors and school
years. Both restored three schools, matched source populated-document/school
counts, and served all three schools through the authenticated API. Both manifests
match the reviewed T18 manifests byte for byte; downloaded package hashes match
verification and provenance.

[SQL Server Populated evidence](populated-mssql.json) records T23's successful
[non-publishing run](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38346684522).
Both Data Standard legs used fresh SQL Server 2025 sources and backup restoration
targets with `RequirePopulatedData=true` and passed the compatibility-level 170
gate. Source and restored timestamp/compact-descriptor catalog gates passed before
authenticated API probes. DS 5.2.0 restored 101,424 populated documents and DS 6.1.0
restored 100,495, excluding descriptors and school years. Both restored three
schools, matched source populated-document/school counts, and served all three
schools through the authenticated API. Both manifests match the reviewed T18
manifests byte for byte; catalog assertion SQL and its embedded inventory match
the reviewed baseline after ignoring JSON object property order.

All eight artifacts use producing implementation commit
`b0d04812293b415188333fc2e46e79ed5fd0277d`; the subsequent evidence-only commits
are not producing commits. At T23 completion, remote `DMS-1401` still points to
that implementation commit.

| Provider | Kind | DS 5.2.0 core+TPDM | DS 6.1.0 core | Evidence |
| --- | --- | --- | --- | --- |
| PostgreSQL | Minimal | Passed | Passed | [T20](minimal-postgresql.json) |
| SQL Server | Minimal | Passed | Passed | [T21](minimal-mssql.json) |
| PostgreSQL | Populated | Passed | Passed | [T22](populated-postgresql.json) |
| SQL Server | Populated | Passed | Passed | [T23](populated-mssql.json) |

The evidence records package identities, SHA-256 hashes, exact reviewed model
manifest hashes, schema input hashes, engine identity, verification results,
and downloadable package/verification/SBOM/provenance artifact IDs. Downloaded
package hashes match both workflow verification records and provenance subjects;
provenance metadata identifies the producing commit and `publish_package=false`.
All eight model manifests match the reviewed T18 manifests byte for byte.

Packages are version `8.1.0-beta.0.1.35` and remain unpublished. GitHub artifacts
have the expiration times recorded in the evidence. Local downloads and check
records are retained at `/tmp/t20-results/` (PostgreSQL Minimal), `/tmp/t21-results/`
(SQL Server Minimal), `/tmp/t22-results/` (PostgreSQL Populated), and
`/tmp/t23-results/` (SQL Server Populated); copy them before cleaning those
directories if needed for later release work. T23 also rechecked the
retained T20–T22 package hashes against their records when reconciling this matrix.

T20–T23 qualify all eight required matrix entries. [T24's consumer inventory](consumers.json)
found no affected fixed repository package-version pins. Compose/bootstrap uses
generated DDL and API seed delivery; smoke consumes artifacts built in the same
run. The retained manual loader and Azure VM examples select versions dynamically
or from operator input. Package-ID Data Standard suffixes are unchanged.

T24 therefore applies its explicit no-pins exception: no new pins or package
publication, feed downloads, or additional consumer restores. All eight retained
package hashes were rechecked against the records above. Dynamic `latest` selection
is not certified as compatible with this branch; changed-runtime deployments still
need databases freshly provisioned from the changed DDL or compatible packages verified through the release
workflow. Actual deployment updates, stable publication and release-view promotion
remain release responsibilities. All implementation-loop tasks are complete.
