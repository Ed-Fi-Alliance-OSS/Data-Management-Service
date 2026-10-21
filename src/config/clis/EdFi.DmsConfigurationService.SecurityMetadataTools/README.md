# Ed-Fi API Security Metadata Tools

A .NET 10 command-line tool for converting ODS security metadata XML exports to
Ed-Fi API Configuration Service claims documents.

## Development status

The command host and package structure are available. Conversion, validation, and
output writing are under development in DMS-1617. Conversion currently exits with
an error and does not write output. This package is not ready for operational use.

```sh
security-metadata-tools --help
security-metadata-tools --version
security-metadata-tools convert-ods-xml --help
```

The conversion command requires `--input <xml>` and `--output <json>`.
`--force` requests replacement of an existing destination after successful
conversion and validation. These options establish the command contract; the
conversion handler will be implemented in subsequent tasks.
