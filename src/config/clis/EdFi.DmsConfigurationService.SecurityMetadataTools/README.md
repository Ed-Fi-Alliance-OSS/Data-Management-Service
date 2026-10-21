# Ed-Fi API Security Metadata Tools

A .NET 10 command-line tool for converting ODS security metadata XML exports to
Ed-Fi API Configuration Service claims documents.

## Development status

Conversion, CMS schema validation, output writing, upload acceptance coverage,
and local package build/installation checks are implemented. Release automation
remains under development in DMS-1617; this tool has not yet been released.

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

## Maintainer versioning

See [VERSIONING.md](VERSIONING.md) for the committed tool patch, shared version
calculation, release preparation and recovery policy, and current build/workflow
implementation status.
