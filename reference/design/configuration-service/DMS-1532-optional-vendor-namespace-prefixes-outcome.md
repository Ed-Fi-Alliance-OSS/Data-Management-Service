# DMS-1532: Optional vendor namespace prefixes — ticket outcome

CMS accepts empty or omitted `namespacePrefixes` when creating a vendor with
`POST /v3/vendors` or updating one with `PUT /v3/vendors/{id}`. Explicit JSON null,
whitespace, and delimiter-only values normalize to an empty string. Supplied prefixes
are trimmed and empty entries removed; each effective prefix must still be at most
128 characters. GET represents a vendor without prefixes with `namespacePrefixes: ""`.
Omitting the field on PUT clears all existing prefixes and re-applies the empty namespace
claim to the vendor's existing clients. Prefix changes affect newly issued tokens;
previously issued tokens retain their claims until expiry.

A no-prefix vendor may have applications using claim sets that contain NamespaceBased
authorization. For newly provisioned clients and clients whose vendor prefixes are updated,
newly issued tokens contain a present string claim `"namespacePrefixes": ""`,
and DMS interprets it as zero configured prefixes without failing token validation.
For a correctly configured resource and an operation governed by NamespaceBased, DMS
fails closed with HTTP 403 and problem type
`urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces` until
matching prefixes are assigned. Operations using other authorization strategies retain
their existing behavior; empty prefixes grant no namespace-derived access.

Both identity providers retain an empty namespace claim on client creation and vendor
prefix updates. Keycloak encodes the empty value in its hardcoded mapper as the JSON
string literal `""` with type `JSON`, because an empty mapper configuration value is
omitted by its Admin REST path. Non-empty values use type `String`. Keycloak admin clients
registered after the upgrade also use this representation. Existing admin clients are
not automatically updated; `/connect/register` rejects an already registered client.
This aligns only the `namespacePrefixes` claim with the self-contained provider:
Keycloak still omits an empty `educationOrganizationIds` claim.

Vendor POST remains create-only under DMS-1341. A company already present in the caller's
tenant returns 400 with the error under `validationErrors.Company`, leaving the vendor
and its client claims unchanged even if prefixes are omitted. Updates use PUT and retain
the [DMS-1356 workflow](DMS-1356-vendor-namespace-update-consistency.md): application
locks, provider-first mutation, guarded UUID synchronization, repository commit last,
and compensation on failure. No schema, migration, or relational mapping change is needed.
