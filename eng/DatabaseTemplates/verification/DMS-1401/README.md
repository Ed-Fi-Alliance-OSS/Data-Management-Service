# DMS-1401 template qualification

[PostgreSQL Minimal evidence](minimal-postgresql.json) records T20's successful
[non-publishing run](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38343258018).
Both DS 5.2.0 core+TPDM and DS 6.1.0 core used fresh PostgreSQL 16.8 sources and
fresh restoration targets. Source and restored timestamp/compact-descriptor
catalog gates, schema/data checks, and authenticated descriptor reads passed.

The producing implementation commit is
`b0d04812293b415188333fc2e46e79ed5fd0277d`. T21–T23 must dispatch a ref pointing to
this same commit; an evidence-only commit is not the producing commit. At T20
completion, remote `DMS-1401` still points to that implementation commit.

The evidence records package identities, SHA-256 hashes, exact reviewed model
manifest hashes, schema input hashes, engine identity, verification results,
and downloadable package/verification/SBOM/provenance artifact IDs. Downloaded
package hashes match both workflow verification records and provenance subjects;
provenance metadata identifies the producing commit and `publish_package=false`.
Both model manifests match the reviewed T18 manifests byte for byte.

Packages are version `8.1.0-beta.0.1.35` and remain unpublished. GitHub artifacts
have the expiration times recorded in the evidence. Local downloads and check
records are retained at `/tmp/t20-results/`; copy them before cleaning that
directory if needed for the later publication task.

T20 qualifies two of the eight required matrix entries. SQL Server Minimal,
both Populated providers, and verified prerelease publication/consumer pins
remain T21–T24. No story closure or release promotion is claimed here.
