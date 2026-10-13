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

The producing implementation commit is
`b0d04812293b415188333fc2e46e79ed5fd0277d`. T23 must dispatch a ref pointing to
this same commit; an evidence-only commit is not the producing commit. At T22
completion, remote `DMS-1401` still points to that implementation commit.

The evidence records package identities, SHA-256 hashes, exact reviewed model
manifest hashes, schema input hashes, engine identity, verification results,
and downloadable package/verification/SBOM/provenance artifact IDs. Downloaded
package hashes match both workflow verification records and provenance subjects;
provenance metadata identifies the producing commit and `publish_package=false`.
All six model manifests match the reviewed T18 manifests byte for byte.

Packages are version `8.1.0-beta.0.1.35` and remain unpublished. GitHub artifacts
have the expiration times recorded in the evidence. Local downloads and check
records are retained at `/tmp/t20-results/` (PostgreSQL Minimal), `/tmp/t21-results/`
(SQL Server Minimal), and `/tmp/t22-results/` (PostgreSQL Populated); copy them
before cleaning those directories if needed for the later publication task.

T20–T22 qualify six of the eight required matrix entries. SQL Server Populated
and verified prerelease publication/consumer pins remain T23–T24.
No story closure or release promotion is claimed here.
