# Ed-Fi API Security Metadata Tools

A .NET 10 command-line tool for converting ODS security metadata XML exports to
Ed-Fi API Configuration Service claims documents.

## Usage

```sh
security-metadata-tools --help
security-metadata-tools --version
security-metadata-tools convert-ods-xml --help
security-metadata-tools convert-ods-xml --input security-metadata.xml --output Claims.json
```

The conversion command requires `--input <xml>` and `--output <json>`.
`--force` permits replacement of an existing destination after successful
conversion and validation. Conversion writes a complete document with `claimSets`
and `claimsHierarchy` to the output file and exits zero without writing JSON to
stdout. Failures exit nonzero with diagnostics on stderr. Conversion and validation
failures leave existing destination bytes unchanged and do not create a new
destination. The destination's parent directory must already exist.

## Conversion rules

Support is limited to the [verified ODS XML export format and provenance](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/tree/main/src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit/Fixtures/OdsSecurityMetadata).

- Claim-set names lose only literal spaces (`U+0020`) and hyphens (`U+002D`).
  Remaining whitespace, including nonbreaking spaces, and control characters are
  rejected. Names must contain 1–256 UTF-16 code units after normalization;
  remaining spelling, case, and valid non-ASCII characters are preserved.
  Distinct source names that collide case-insensitively fail conversion.
- Catalog entries and hierarchy references use the same mapped names. Every
  emitted catalog entry has `isSystemReserved: false`; uploading an existing
  reserved name preserves its stored reserved status.
- Only the exact, case-sensitive leading `http://ed-fi.org/ods/identity/claims/`
  prefix migrates to `http://ed-fi.org/identity/claims/`. A nonempty suffix with
  no `/` also gains `ed-fi/`: legacy `academicWeek` becomes
  `http://ed-fi.org/identity/claims/ed-fi/academicWeek`. Domain, service, and
  already-namespaced suffixes retain their paths. Already-current, external,
  and near-match URIs remain unchanged. URI collisions fail conversion.
- Nested policy, actions, authorization strategy names, and overrides are
  preserved. Populated `validationRuleSet` and `validationRuleSetOverride`
  attributes fail conversion; absent or empty values are accepted.

Conversion validates the complete document against the CMS claims schema.
Conversion and loading do not establish runtime support for every imported
strategy; review compatibility with the Ed-Fi API deployment enforcing it.

## Maintainer versioning

See [VERSIONING.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/VERSIONING.md) for the committed tool patch, shared version
calculation, release preparation, and recovery policy.
