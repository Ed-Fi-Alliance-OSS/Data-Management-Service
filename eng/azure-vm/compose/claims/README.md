# Claim sets

A claim set is the authorization profile attached to an API client (key/secret) when you
create an Application in the Configuration Service. It controls which resources the client
can touch and under which authorization strategy.

## Built-in (embedded) claim sets

Available out of the box (`DMS_CONFIG_CLAIMS_SOURCE=Embedded`). Confirm the live list with
`GET /<config>/v3/claimSets`:

| Claim set | Authorization |
|-----------|---------------|
| `EdFiSandbox` | Broad access (the `bootstrap.ps1` default). Education organizations are unrestricted, descriptor writes are namespace-based, and relationship-based data is scoped to the Application's `educationOrganizationIds` |
| `SISVendor`, `DistrictHostedSISVendor`, `RosterVendor`, `AssessmentVendor`, ... | Vendor-specific scopes; inspect each with `GET /<config>/v3/authorizationMetadata?claimSetName=<name>` |

The test-only `E2E-*` claim sets are not built in; they exist only in the repository's E2E test
setup.

Extension claim sets (`SampleExtensionClaims`, `HomographExtensionClaims`) are **not** embedded —
they ship only as filesystem fragments, so they are available only via **Hybrid** mode (below).

## School / district-level access

Use `EdFiSandbox` and bind the Application to specific
`educationOrganizationIds` (e.g. district `255901` = Grand Bend ISD, or a school like
`255901001`). The client then only sees relationship-based data (students, sections,
enrollments, ...) for those EdOrgs and their descendants.
`bootstrap.ps1` provisions these per deployment through
[`bootstrap/review-variants.psm1`](../bootstrap/review-variants.psm1): `SISVendor` at district
`255901`, `SISVendor` at school `255901107`, `AssessmentVendor` at `255901` with namespace
`uri://one.example.com` (outside the sample data, so it sees none of the `uri://ed-fi.org`
assessments), and `EdFiSandbox` at `255901`. Add them to an older environment with
`bootstrap/add-review-variants.ps1`.

## Creating custom claim sets

Two ways:

1. **API (recommended, no restart):** the Configuration Service exposes full CRUD —
   `POST /v3/claimSets`, `PUT/DELETE /v3/claimSets/{id}`, `POST /v3/claimSets/copy`,
   `POST /v3/claimSets/import`, and `GET /v3/claimSets/{id}/export`. Easiest path:
   `export` an existing claim set as a template, edit, then `import`/`POST` it. Use a CMS
   admin token (see `http/multi-tenant.http`).

2. **File-based:** set `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` in `.env` and drop custom claim set
   fragments in this directory (mounted into both Config Services at `/app/additional-claims`).
   Each file must be named `*-claimset.json` and hold `{ "name": "<ClaimSetName>",
   "resourceClaims": [ ... ] }` — see the "A fragment that defines a claim set" example in
   `docs/CLAIMS-LOADING-GUIDE.md`. Its resource claims must not be `"isParent": true`: a fragment
   with only parent entries (like the built-in fragments under
   `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/`) attaches
   grants to existing claim sets and creates no claim set. This is **not** the API
   `export`/`import` shape (which uses `claimSetName`), so start from that example rather than an
   API export. Restart the `*-config` services (or use the management reload).

> Keep this directory free of partial/invalid fragments while in Hybrid mode — a malformed
> claim set can fail CMS startup. Files not named `*-claimset.json` (like this README) are ignored.
