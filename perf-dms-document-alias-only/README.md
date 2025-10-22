# Document & Alias Data Harness

This directory contains tooling to generate large Ed-Fi `Document` payloads with matching `Alias` rows and bulk load them into the `edfi_datamanagementservice` PostgreSQL database. The payloads mirror the canonical schema and use expanded JSON bodies derived from the Ed-Fi OpenAPI specification.

## Contents
- `data/generate_document_alias_data.py` – Python generator that emits `document.csv` and `alias.csv` files with a 1:1 relationship between tables.
- `scripts/load-document-alias-from-csv.sh` – Loader script that truncates `dms.Document`/`dms.Alias` and uses `COPY` to ingest the generated CSVs.
- `scripts/config.sh` – Shared configuration for database connection settings and helper utilities.

## Setup

### Virtual Environment
The data generation script requires Python 3 and the `uuid7` package. A virtual environment is included to manage dependencies:

```bash
# The venv is already set up. To recreate it:
python3 -m venv venv

# Install dependencies
./venv/bin/pip install -r requirements.txt
```

### Dependencies
- `uuid7` - For generating UUIDv7 identifiers

## Generating CSV files
```bash
cd perf-dms-document-alias-only
./venv/bin/python3 data/generate_document_alias_data.py \
  --documents 10000 \
  --openapi ./swagger.json \
  --output ./data/out
```

Key options:
- `--documents` (and the matching `--aliases`) control how many paired rows are emitted. A strict 1:1 mapping is enforced.
- `--resource-cycle` lets you override the default rotation of `sections`, `assessments`, and `studentAcademicRecords`. Entries can be written as `resourceName` or `resourceName:schemaName` when targeting additional schemas present in the OpenAPI file.
- `--openapi` defaults to the bundled `./swagger.json`, but you can supply an alternate specification when experimenting with other resource sets.
- `--resource-version` and `--project-name` let you customize the `Document` metadata, while `--partitions` defines the hash modulus applied to UUIDs.

The generator expands every property defined for the selected resources (including nested collections) and produces deterministic values using the Ed-Fi OpenAPI specification (checked in at `./swagger.json` by default). All payloads include consistent `_etag` and `_lastModifiedDate` attributes, and the accompanying security envelope contains trace metadata for downstream testing.

## Loading data into PostgreSQL
```bash
cd perf-dms-document-alias-only
export DB_USER=postgres
export DB_PASSWORD=your_password
export DB_HOST=localhost
export DB_PORT=5432
export DB_NAME=edfi_datamanagementservice
scripts/load-document-alias-from-csv.sh ./data/out
```

The loader performs the following steps:
1. Temporarily disables autovacuum on `dms.Document`, `dms.Alias`, and their partitions.
2. Truncates the tables (resetting identities) and bulk loads the CSV data via `COPY`.
3. Re-enables autovacuum, analyzes both tables, and reports row counts.

If no directory argument is provided, the script uses `OUTPUT_DIR` from `scripts/config.sh` (default: `${SCRIPT_ROOT}/data/out`). Database credentials are read from environment variables; `DB_PASSWORD` honors the ambient `PGPASSWORD` and otherwise falls back to `abcdefgh!`.

## Notes
- The generator writes CSV headers compatible with the production DMS schema; no reference-table rows are produced.
- Generated sample data can be removed once validated; by default it lands under `perf-dms-document-alias-only/data/out` unless an alternate path is supplied.
- Ensure Docker or local PostgreSQL is running before invoking the loader.
