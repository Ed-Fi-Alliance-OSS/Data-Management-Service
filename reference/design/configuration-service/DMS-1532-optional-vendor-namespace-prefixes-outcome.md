# DMS-1532: Optional vendor namespace prefixes — ticket outcome

CMS accepts empty or omitted `namespacePrefixes` when creating a vendor with
`POST /v3/vendors` or updating one with `PUT /v3/vendors/{id}`. Explicit JSON null,
whitespace, and delimiter-only values normalize to an empty string. Supplied prefixes
are trimmed and empty entries removed; each effective prefix must still be at most
128 characters. GET represents a vendor without prefixes with `namespacePrefixes: ""`.

A no-prefix vendor may have applications using claim sets that contain NamespaceBased
authorization. The issued token contains a present string claim `"namespacePrefixes": ""`,
and DMS interprets it as zero configured prefixes without failing token validation.
For a correctly configured resource and an operation governed by NamespaceBased, DMS
fails closed with HTTP 403 and problem type
`urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces` until
matching prefixes are assigned. Operations using other authorization strategies retain
their existing behavior; empty prefixes grant no namespace-derived access.

Both identity providers retain an empty namespace claim. Keycloak encodes the empty
value in its hardcoded mapper as the JSON string literal `""` with type `JSON`, because
an empty mapper configuration value is omitted by its Admin REST path. Non-empty
values use type `String`. The same representation applies to Keycloak admin clients,
bringing their empty-claim shape into line with the self-contained provider.

Vendor POST remains create-only under DMS-1341. A company already present in the caller's
tenant returns 400 with the error under `validationErrors.Company`, leaving the vendor
and its client claims unchanged even if prefixes are omitted. Updates use PUT and retain
the [DMS-1356 workflow](DMS-1356-vendor-namespace-update-consistency.md): application
locks, provider-first mutation, guarded UUID synchronization, repository commit last,
and compensation on failure. No schema, migration, or relational mapping change is needed.

## Design reconciliation

The story's four acceptance criteria are unchanged. The historical specification at
`622e0d912` was deleted in `302247e70`; it rejected explicit null and excluded normalization.
The implementation now accepts and normalizes null, consistent with the story's cited
Admin API null-valid behavior. This outcome records that broader accepted-input contract
without changing the acceptance criteria or claiming that the historical decision was
re-approved. Normalizing effective prefixes before persistence and provider propagation
keeps both boundaries consistent for whitespace and empty entries.

The Keycloak mapper correction is necessary for the story's present-empty token claim;
the historical specification was already amended for it in `9de241e63`. The historical
201/200 POST assumption predates DMS-1341 and is superseded by its create-only contract.
The changelog entry documents the accepted inputs and token shape for release users.

The supplied story has no epic identifier or sibling inventory. Related repository
evidence inspected includes DMS-1039, DMS-1218, DMS-1356, DMS-1513, DMS-1341, and
DMS-1557; these are related contracts, not a claimed exhaustive list of epic siblings.
DMS-428 relocated the pre-existing non-empty validation rule, rather than introducing it.

## Verification boundary

Source and regression coverage have been reviewed against the acceptance criteria.
Coverage includes command and HTTP empty/omitted/null inputs and length boundaries;
PostgreSQL and SQL Server empty readback and duplicate non-mutation; provider mapper
create/clear/restore; CMS token creation and clearing; DMS empty-claim consumption and
the exact NamespaceBased denial; and repeated POST preserving an existing client claim.
These tests were not run locally during the peer-review-04 remediation, as requested.
This outcome records intended behavior supported by source inspection, not a new runtime
or CI verification claim.
