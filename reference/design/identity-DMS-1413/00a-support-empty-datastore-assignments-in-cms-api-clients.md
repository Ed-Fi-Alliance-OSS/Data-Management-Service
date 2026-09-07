---
jira: DMS-1513
jira_url: https://edfi.atlassian.net/browse/DMS-1513
epic: DMS-1412
source_spike: DMS-1413
---

# Story: Support Empty Datastore Assignments in CMS API Clients

## Description

Allow operators to create and administer identity-only clients through the normal CMS API-client lifecycle. Application creation already accepts an empty datastore list and creates an initial client, but `ApiClientInsertCommand.Validator` and `ApiClientUpdateCommand.Validator` reject empty assignments. Correct that mismatch before the identity API surface lands.

This is a CMS prerequisite. It adds no identity backend, identity routes, new claim, or database migration. Story 02 owns the end-to-end proof from these CMS endpoints through token issuance to DMS identity requests.

## Acceptance Criteria

- Application creation continues to allow an explicit `DataStoreIds: []` and creates an initial client with no datastore assignment.
- Direct API-client creation and update accept an explicit empty datastore list, including retaining an empty list and removing the final existing assignment. An omitted list retains the commands' existing empty-list default; explicit JSON null is rejected as validation failure rather than reaching endpoint array access.
- Nonempty assignments retain tenant-scoped datastore existence validation. Other client/application validation, approval, ownership configuration, credential handling, and tenant isolation remain unchanged.
- Real CMS endpoint tests create an application with an initial empty-assignment client, add another client through `/v3/apiClients`, retrieve both, update them while retaining empty lists, reset credentials through `/v3/apiClients/{id}/reset-credential`, and delete the additional client. Tests assert persisted assignments remain empty after update/reset and use the current credentials returned by each operation.
- CMS tests also cover clearing a populated assignment, invalid/null assignments, and cross-tenant access. Removing the minimum-count rule never bypasses checks for supplied datastore ids.
- CMS OpenAPI/client documentation and examples describe empty assignments consistently with the validators.
- The story-02 integration proof confirms a token from an approved client with no datastore assignment can call authorized identity operations but gains no datastore/resource access. No resource authorization middleware is relaxed to accommodate empty lists.

## Tasks

1. Update the API-client insert/update validators and their unit coverage for empty and null lists.
2. Exercise application/client persistence, update, credential reset, and deletion through CMS endpoints; make only changes needed to preserve empty assignments on those paths.
3. Update CMS contract documentation/examples and retain nonempty-assignment and tenant-isolation regression coverage.
4. Provide the CMS provisioning flow used by story 02's token-to-identity integration proof.
