---
jira: DMS-1058
jira_url: https://edfi.atlassian.net/browse/DMS-1058
---

# Spike: Design Ownership-token Maintenance in CMS

## Description

CMS needs to be updated to store Ownership tokens of ApiClients in order to implement the Ownership-based authorization strategy in DMS.

Refer to the authorization design for more information: `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Review how ODS stores the Ownership tokens of a given ApiClient and propose a storage design for CMS.
- Propose what endpoints need to be updated or created to maintain the Ownership tokens of an ApiClient.
  - Consider that neither the Admin API nor the Admin App supports this, so the endpoint design will be brand new.
- Propose how DMS will read and cache the Ownership tokens from CMS.
- Once the proposals above are reviewed and approved, create the tickets that implement the changes.
