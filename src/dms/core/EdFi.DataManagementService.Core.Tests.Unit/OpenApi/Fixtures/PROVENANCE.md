# ods-7.3.2-identity-openapi.json provenance

Source URL: `https://api.ed-fi.org/v7.3/api/metadata/identity/v2/swagger.json`.
Fetch date: 2026-09-22.
Byte size: 13,507 bytes, fetched with `curl` and checked in unmodified.
OpenAPI version: `3.0.1` (declared in the document's own `openapi` field).

This is the identity metadata document served by the reference Ed-Fi ODS / API deployment at
`api.ed-fi.org/v7.3`, which runs Ed-Fi-ODS tag `v7.3.2`.
Its `paths` keys, its six component names
(`IdentityCreateRequest`, `IdentityResponse`, `IdentitySearchRequest`, `IdentitySearchResponse`,
`IdentitySearchResponses`, `Location`), and every property `type` and `format` in those six components
are identical to `Application/EdFi.Ods.Features/Resources/IdentityManagement.json` at
`https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Features/Resources/IdentityManagement.json`,
which is a Swagger 2.0 document.
The pinned fixture is the Swagger 2.0 source after the ODS runtime's own conversion to OpenAPI 3.0.1,
which is why it carries `openapi: 3.0.1` rather than `swagger: 2.0`.

`IdentityOpenApiOdsCompatibilityTests` walks this fixture against the served DMS identity document and
asserts every remaining difference is named in its encoded ledger.
