# Canonical JSON Serialization

This document describes the canonical JSON serialization rules used by DMS for
deterministic hashing of API schemas.

## Purpose

Canonical JSON serialization ensures that semantically identical JSON documents
produce byte-for-byte identical output, enabling deterministic hash computation.
This is used by:

- **Effective Schema Hash**: Verifies database instances match the DMS schema version
- **Future mapping-pack determinism checks**: Ensures consistent schema derivation

## Canonicalization Rules

### 1. Object Property Ordering

JSON object properties are recursively sorted by property name using
`StringComparer.Ordinal` semantics (Unicode code point order).

```json
// Input (any order)
{ "zebra": 1, "alpha": 2, "Beta": 3 }

// Canonical output (ordinal sort: uppercase before lowercase)
{"Beta":3,"alpha":2,"zebra":1}
```

### 2. Array Element Order

JSON arrays preserve their element order exactly. Arrays are **not** sorted.

```json
// Input
["third", "first", "second"]

// Canonical output (order preserved)
["third","first","second"]
```

### 3. Whitespace

All insignificant whitespace is removed (minified form). No spaces, newlines,
or indentation appear in the output.

### 4. Encoding

Output is UTF-8 encoded without a Byte Order Mark (BOM).

### 5. Nested Structures

Canonicalization is applied recursively to all nested objects and arrays.

```json
// Input
{
  "outer": {
    "z": 1,
    "a": 2
  },
  "array": [
    { "y": 3, "x": 4 }
  ]
}

// Canonical output
{"array":[{"x":4,"y":3}],"outer":{"a":2,"z":1}}
```

### 6. Null Values

Null values are preserved in the output as the literal `null`.

```json
// Input
{ "present": "value", "absent": null }

// Canonical output
{"absent":null,"present":"value"}
```

## Effective Schema Hash Computation

The effective schema hash is computed as follows:

1. **Normalize inputs**: The `ApiSchemaInputNormalizer` strips OpenAPI payloads
   and sorts extensions by `projectEndpointName` (ordinal).

2. **Combine schemas**: Core and extension schemas are wrapped in a structure:
   ```json
   {
     "coreSchema": { ... },
     "extensionSchemas": [ ... ]
   }
   ```

3. **Canonicalize**: The combined structure is serialized using canonical JSON.

4. **Hash**: SHA-256 is computed over the canonical UTF-8 bytes.

5. **Format**: The hash is output as a 64-character lowercase hexadecimal string.

## Implementation

- **Canonicalizer**: `EdFi.DataManagementService.Core.Utilities.CanonicalJsonSerializer`
- **Hash Provider**: `EdFi.DataManagementService.Core.Startup.EffectiveSchemaHashProvider`

## Usage

### At DMS Startup

The effective schema hash is computed during startup and logged:

```
Effective schema hash: a1b2c3d4e5f6...
```

### Via SchemaTools CLI

```bash
dms-schema core/ApiSchema.json extensions/tpdm/ApiSchema.json
```

Output includes:
```
Schema normalization successful.
Effective schema hash: a1b2c3d4e5f6...
```

## Resource ETag Format and Quoting Convention

Resource `_etag` values served by DMS are **unquoted** Base64-encoded SHA-256 hashes, for
example:

```
ETag: R2aCXlHmGFXaM2OJ+SBjzA==
```

### Computation

1. Deep-clone the document body.
2. Remove `id`, `_etag`, and `_lastModifiedDate` fields.
3. Canonicalize the remaining object (ordinal property sort, minified UTF-8).
4. Compute `SHA-256` over the canonical bytes.
5. Base64-encode the raw bytes (Standard encoding, no padding strip).

Implementation: `EdFi.DataManagementService.Backend.RelationalApiMetadataFormatter.FormatEtag`.

### Quoting Deviation From RFC 7232

RFC 7232 section 2.3 specifies that an ETag value must be enclosed in double quotes.
DMS ETags are intentionally **unquoted** to preserve behavioral parity with the legacy
ODS implementation, which clients depend on.

This deviation is tracked for future RFC-conformance work. A client sending an `If-Match`
header must supply the raw unquoted Base64 string exactly as received in the `_etag` field
or `ETag` response header. No surrounding quotes should be added.

> **Future work**: Adopt RFC 7232-conformant quoting and update the `If-Match` comparison
> to strip surrounding double quotes before ordinal comparison.
> See the Ed-Fi Jira backlog for a future RFC-conformance ticket.

### If-Match Wildcard (`*`)

RFC 7232 section 3.1 defines the `*` wildcard to mean "any existing representation".
DMS does **not** support that wildcard form.

This is intentional for compatibility with Ed-Fi ODS/API behavior. The ODS/API documents
`If-Match` using concrete `_etag` values and does not document support for wildcard
preconditions. To avoid introducing a concurrency contract that diverges from ODS/API,
DMS requires clients to send the exact `_etag` value returned by a prior read.

As a result:

- `If-Match: *` is treated as unsupported and does not act as "match any existing representation".
- Clients should send the concrete unquoted Base64 `_etag` value exactly as received.
- Blank or malformed `If-Match` values are not treated as an unconditional write signal.

Implementation: DMS compares client `If-Match` values against the computed current `_etag`
for guarded update and delete flows and does not provide special wildcard bypass semantics.

## Verification

To verify canonicalization is working correctly:

1. Create two JSON files with identical content but different formatting or ordering
2. Run both through the canonicalizer
3. The output bytes should be identical
4. The computed hashes should match
