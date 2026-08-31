# DocumentCache References

This folder contains DMS-1317-owned references for the durable E18 DocumentCache
implementation, operations, evidence, and qualification work.

- [Operations runbook](operations-runbook.md) documents status interpretation, activation,
  deactivation, rebuild, scrub, cache-ahead recovery, SQL Server prerequisite correction,
  and restore/direct-mutation response.
- [CDC-INV evidence matrix](cdc-inv-evidence.md) maps DMS-1317 evidence to the in-scope
  E18 CDC invariant contracts.
- [Performance qualification](performance-qualification.md) defines the bounded CI guards,
  representative-scale thresholds, result artifacts, and provider maintenance evidence.

Owning design and implementation references:

- [DMS-1317 story](../design/backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md)
- [Backend redesign summary](../design/backend-redesign/design-docs/summary.md)
- [DocumentCache and CDC design](../design/backend-redesign/design-docs/cdc/cdc-streaming.md)
- [DocumentCacheAdmin CLI story](../design/backend-redesign/epics/18-document-cache/09-documentcache-administration-cli.md)
- [DocumentCacheAdmin CLI README](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md)
- [DMS configuration](../../docs/CONFIGURATION.md#datamanagementdocumentcache)
- [Relational backend guide](../../docs/RELATIONAL-BACKEND.md#always-provisioned-documentcache-inventory)

DMS-1317 material stops at the DocumentCache projection boundary. Kafka connector
operation, binding/source history, topic management, source replacement, consumer-state
recovery, and downstream publication containment are E19 concerns; start with the
[E19 CDC runbook story](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md).
