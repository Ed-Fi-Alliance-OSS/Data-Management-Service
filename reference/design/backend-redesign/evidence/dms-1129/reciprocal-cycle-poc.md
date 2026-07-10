# Reciprocal-Cycle POC (Historical)

The DMS-1129 design briefly carried provider integration tests for a two-table reciprocal identity cycle. They established
two useful database facts:

- SQL Server rejects two reciprocal `ON UPDATE CASCADE` foreign keys with error 1785. With one edge changed to
  `NO ACTION`, updates from only the retained-cascade origin can propagate while the reverse-origin update fails with
  error 547.
- PostgreSQL can install reciprocal full-vector cascades and propagated the tested primitive key changes.

These observations do not define a supported DMS model. DMS rejects this semantic identity cycle before vector derivation
on either provider, so it never reaches physical action assignment. Separately, a semantic-identity-acyclic model can
still produce a SQL Server physical update-cascade cycle through otherwise-valid mutual non-identity references. SQL
Server reports that case from its normal all-native topological legality pass as
`SqlServerCascadeCycleNotSupported`; PostgreSQL performs no corresponding physical-topology rejection. The executable
reciprocal identity-cycle POCs were therefore removed from the normal integration projects. Production tests cover the
provider-independent semantic guard and the distinct SQL Server physical-cycle outcome.

The removed SQL remains available in Git history if a provider behavior needs to be reproduced again.
