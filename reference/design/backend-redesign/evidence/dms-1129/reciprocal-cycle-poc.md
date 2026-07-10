# Reciprocal-Cycle POC (Historical)

The DMS-1129 design briefly carried provider integration tests for a two-table reciprocal identity cycle. They established
two useful database facts:

- SQL Server rejects two reciprocal `ON UPDATE CASCADE` foreign keys with error 1785. With one edge changed to
  `NO ACTION`, updates from only the retained-cascade origin can propagate while the reverse-origin update fails with
  error 547.
- PostgreSQL can install reciprocal full-vector cascades and propagated the tested primitive key changes.

These observations do not define a supported DMS model. MetaEd must reject this authored semantic identity cycle, and DMS
rejects it again as a cycle in the post-key-unification effective identity graph before vector derivation on either
provider. The DMS graph also promotes a non-identity reference when its canonical local storage overlaps receiver public
identity storage, so a mutual promoted pair fails at the same provider-independent boundary.

Separately, otherwise-valid mutual non-identity references whose mapped local storage is disjoint from both receiver
propagation keys can produce a broader SQL Server physical update-cascade cycle while both edges remain certified
origin-terminal. SQL Server reports that distinct case from its normal all-native topological legality pass as
`SqlServerCascadeCycleNotSupported`; PostgreSQL performs no corresponding physical-topology rejection. The executable
reciprocal identity-cycle POCs were therefore removed from the normal integration projects. Production tests must cover
authored and storage-promoted effective-cycle rejection plus the distinct origin-terminal SQL Server physical-cycle
outcome.

The removed SQL remains available in Git history if a provider behavior needs to be reproduced again.
