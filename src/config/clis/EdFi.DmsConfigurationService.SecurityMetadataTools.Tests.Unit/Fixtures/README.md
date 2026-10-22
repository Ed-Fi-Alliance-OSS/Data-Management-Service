# Conversion test scope

`OdsXmlToClaimsConverterTests.cs` contains handwritten synthetic XML and independent
expected JSON literals. These are focused unit examples, not a database export or
the representative acceptance oracle tracked in `OdsSecurityMetadata/README.md`.

The element mapping was checked against the [pinned ODS SQL Server export script](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/blob/5bca6615e1ce06b84a87e8512cfbe69a1249dd9a/SecurityMetadata/Export/MsSql/Security-Metadata-to-XML.sql)
and the existing CMS contract at
`src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/JsonSchemaForClaims.json`.

| Export structure | CMS document mapping |
| --- | --- |
| `SecurityMetadata/Claims/Claim` | `claimsHierarchy` entries |
| `Claim/@name` | Claim `name`: migrate the exact leading `http://ed-fi.org/ods/identity/claims/` to `http://ed-fi.org/identity/claims/`; also add `ed-fi/` for a nonempty single-segment suffix (legacy `academicWeek` becomes `http://ed-fi.org/identity/claims/ed-fi/academicWeek`). Preserve domain, service, and already-namespaced suffixes and already-current/external URIs |
| Nested `Claim/Claims/Claim` | Nested `claims` entries, retaining order and inheritance |
| `Claim/@claimId` | Recognized export metadata; omitted from JSON |
| `Claim/DefaultAuthorization/Action/@name` | `defaultAuthorization.actions[].name` |
| Default action `AuthorizationStrategies/AuthorizationStrategy/@name` | `authorizationStrategies[].name`, preserving all names and their order |
| Empty `DefaultAuthorization` | Omitted `defaultAuthorization`; the CMS schema requires at least one action when present |
| Default action with no strategies | Required `authorizationStrategies: []` |
| `Claim/ClaimSets/ClaimSet/@name` | Remove literal spaces/hyphens; reject remaining whitespace (including nonbreaking spaces), controls, empty names, or names over 256 UTF-16 code units; use the same mapped name in `claimSets[].name` and one top-level catalog entry with `claimSetName` and `isSystemReserved: false` |
| Claim set `Actions/Action/@name` | Reference `actions[].name` |
| Claim-set action `AuthorizationStrategyOverrides/AuthorizationStrategy/@name` | `authorizationStrategyOverrides[].name`, preserving all names and their order |
| Empty override container | Explicit `authorizationStrategyOverrides: []` |
| Absent override container | Omitted `authorizationStrategyOverrides` |

The cases exercise two roots, three hierarchy levels, a container node, repeated
claim-set references, a set first referenced by a child, a system-like set name,
custom strategy names, inherited defaults/actions, absent and empty defaults,
empty strategies/overrides, and independent conversion calls.

`OdsXmlToClaimsConverterNameMappingTests.cs` covers exact name normalization,
case-insensitive collisions across the tree, repeated references, 256/257-character
boundaries after normalization, control and remaining-whitespace rejection
(including nonbreaking spaces), valid Unicode and case preservation, project-qualified
legacy resource migration, preserved domain/service/namespaced paths, near matches,
and URI collisions in both traversal orders. Mapping indexes are isolated per
conversion. The representative test reads the checked-in `OdsSecurityMetadata/expected-claims.json` unchanged and compares
the entire document, including array order, with the converted export. It never
regenerates the expected document.

`OdsXmlToClaimsConverterValidationTests.cs` uses small synthetic files in
`Validation/` plus focused mutations and XML literals to cover malformed XML,
missing/empty names, unsupported roots, namespaces, elements, attributes and text,
duplicate singleton containers, malformed actions, and all three empty-policy
conditions (no claims, no referenced sets, no claim-set actions). Diagnostics
include XML paths with escaped names and source line/column when available.

The pinned exporter emits `validationRuleSet` on default actions and
`validationRuleSetOverride` on claim-set actions. Absent and empty attributes are
accepted; every nonempty value, including whitespace, is rejected because the CMS
claims contract cannot represent it. Unknown content is rejected before mapping,
including policy on hierarchy-only nodes. `claimId` remains recognized metadata.

Every converted document passes the existing backend `ClaimsValidator` and its
embedded schema before return. `invalid-generated-actions.xml` has one valid grant
and a second reference with empty actions: it passes export structure and global
completeness checks but fails the real CMS schema's `minItems`. Tests compare the
converter's failure path/messages with independent direct CMS validation to ensure
the final gate cannot be bypassed. Existing positive cases and the representative
comparison also run through this gate; the schema and acceptance oracle are unchanged.

Command tests exercise the actual CLI parsing/handler and process paths, including
validate-before-write behavior, forced overwrite protection, and XML decoding.
These examples do not establish broader export-format support or runtime strategy
compatibility. Prefix-only legacy resource mapping and nonbreaking-space acceptance
are not the supported conversion contract.
