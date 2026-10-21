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
| `Claim/@name` | Claim `name`, with only the leading `http://ed-fi.org/ods/identity/claims/` migrated to `http://ed-fi.org/identity/claims/` |
| Nested `Claim/Claims/Claim` | Nested `claims` entries, retaining order and inheritance |
| `Claim/@claimId` | Recognized export metadata; omitted from JSON |
| `Claim/DefaultAuthorization/Action/@name` | `defaultAuthorization.actions[].name` |
| Default action `AuthorizationStrategies/AuthorizationStrategy/@name` | `authorizationStrategies[].name`, preserving all names and their order |
| Empty `DefaultAuthorization` | Omitted `defaultAuthorization`; the CMS schema requires at least one action when present |
| Default action with no strategies | Required `authorizationStrategies: []` |
| `Claim/ClaimSets/ClaimSet/@name` | Remove literal spaces/hyphens; use the same mapped name in `claimSets[].name` and one top-level catalog entry with `claimSetName` and `isSystemReserved: false` |
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
boundaries after normalization, control characters, Unicode and case preservation,
exact leading URI migration, near matches, and URI collisions in both traversal
orders. Mapping indexes are isolated per conversion. The representative test reads
the checked-in `OdsSecurityMetadata/expected-claims.json` unchanged and compares
the entire document, including array order, with the converted export. It never
regenerates the expected document.

DMS-1617-05 adds policy
completeness/unsupported-content checks (including the script's
`validationRuleSet` and `validationRuleSetOverride` attributes) and the actual CMS
validator. The converter remains internal and is not wired into the CLI until
that validation is available. The examples do not establish broader export-format
support or operational readiness.
