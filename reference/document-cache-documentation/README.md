# DocumentCache References

This folder contains the standalone reference set for the durable DocumentCache
implementation, operations, evidence, and qualification work. It is the index for
the implemented projection-boundary guidance inside a DMS relational data store,
not a Kafka connector or downstream consumer runbook.

- [Operations runbook](operations-runbook.md) documents status interpretation, activation,
  deactivation, rebuild, scrub, cache-ahead recovery, SQL Server prerequisite correction,
  and restore/direct-mutation response.
- [CDC-INV evidence matrix](cdc-inv-evidence.md) maps implemented evidence to the
  in-scope DocumentCache CDC invariant contracts.
- [Performance qualification](performance-qualification.md) defines the bounded CI guards,
  representative-run harness, thresholds, result artifact schema, and provider maintenance
  evidence contract for deferred representative release-validation evidence.
- [Representative qualification runbook](representative-qualification-runbook.md) gives the
  performance engineer release-validation procedure for producing validated result
  artifacts after representative performance execution.

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
[Kafka/CDC operations guidance](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md).
