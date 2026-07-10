# Reciprocal-Cycle POC (Historical)

The DMS-1129 design briefly carried provider integration tests for a two-table reciprocal identity cycle. They established
two useful database facts:

- SQL Server rejects two reciprocal `ON UPDATE CASCADE` foreign keys with error 1785. With one edge changed to
  `NO ACTION`, updates from only the retained-cascade origin can propagate while the reverse-origin update fails with
  error 547.
- PostgreSQL can install reciprocal full-vector cascades and propagated the tested primitive key changes.

These observations do not define a supported DMS model. DMS rejects semantic identity cycles before vector derivation and
storage-mapped physical cycles before provider action selection. The executable reciprocal-cycle POCs were therefore
removed from the normal integration projects; production tests must cover deterministic cycle rejection instead.

The removed SQL remains available in Git history if a provider behavior needs to be reproduced again.
