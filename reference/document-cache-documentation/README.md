# DocumentCache References

This folder contains the standalone reference set for the durable DocumentCache
implementation, operations, and evidence work. It is the index for the implemented
projection-boundary guidance inside a DMS relational data store, not a Kafka connector
or downstream consumer runbook.

- [Operations runbook](operations-runbook.md) documents status interpretation, activation,
  deactivation, rebuild, scrub, cache-ahead recovery, SQL Server prerequisite correction,
  and restore/direct-mutation response.
- [CDC-INV evidence matrix](cdc-inv-evidence.md) maps implemented evidence to the
  in-scope DocumentCache CDC invariant contracts.

Production-scale DocumentCache performance qualification is not included in this
reference set. The executable representative harness, threshold catalog, result schema,
and committed qualification artifacts remain owned by the Projection Performance
Qualification design requirement.

Documentation prose is not treated as an executable CLI contract test. The
DocumentCacheAdmin command names, options, confirmation tokens, JSON shapes, and exit-code
behavior are pinned by the CLI parser, contract, and command-execution tests. Runbook text
and command examples are reviewed as operator guidance instead of being coupled to README
text assertions.

Design and implementation references:

- [Backend redesign summary](../design/backend-redesign/design-docs/summary.md)
- [DocumentCache and CDC design](../design/backend-redesign/design-docs/cdc/cdc-streaming.md)
- [DocumentCacheAdmin CLI README](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md)
- [DMS configuration](../../docs/CONFIGURATION.md#datamanagementdocumentcache)
- [Relational backend guide](../../docs/RELATIONAL-BACKEND.md#always-provisioned-documentcache-inventory)

This material stops at the DocumentCache projection boundary. Kafka connector
operation, binding/source history, topic management, source replacement, consumer-state
recovery, and downstream publication containment are separate Kafka/CDC operations
concerns; start with the
[CDC operator reference](../cdc-documentation/README.md) and its
[projection administration/history handoff](../cdc-documentation/operations-runbook.md#projection-handoff).
The handoff documents the shipped production history reader for `activate-offline`,
`deactivate-offline` and `recover-cache-ahead`; this reference set retains their
projection and offline procedure ownership.

For representation corrections, use the
[CDC restamp handoff](../cdc-documentation/operations-runbook.md#representation-restamp)
to the DocumentCacheAdmin procedure; for previously disclosed sensitive values, use
[sensitive-data response](../cdc-documentation/operations-runbook.md#sensitive-data-response).
The [contract-change owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations)
defines those boundaries. Physical-source replacement remains a
[v1 deferral](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral).
