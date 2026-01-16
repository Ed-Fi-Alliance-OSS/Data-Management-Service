# Backend Redesign: Ahead-of-Time (AOT) Compilation via Mapping Packs

Status: Draft.

This document defines an **optional** alternative to runtime compilation of the JSON→relational mapping described in:

- `reference/design/backend-redesign/flattening-reconstitution.md`
- `reference/design/backend-redesign/data-model.md` (EffectiveSchemaHash)
- `reference/design/backend-redesign/transactions-and-concurrency.md` (per-database schema fingerprint selection)
- `reference/design/backend-redesign/mpack-format-v1.md` (**normative** `.mpack` wire format for PackFormatVersion=1)

The goal is to support **ahead-of-time compilation** into a redistributable artifact (“mapping pack”) that is keyed by `EffectiveSchemaHash`, so a single DMS server can efficiently serve many databases where not all databases share the same effective schema.

---

## 1. Summary

### 1.1 Baseline (runtime compilation)

In the baseline redesign, DMS:

1. Loads `ApiSchema.json` (core + extensions) and builds an in-memory schema model.
2. Derives a **RelationalResourceModel** per resource type from that schema.
3. Compiles **dialect-specific SQL plans** (read/write/identity projection) from the model.
4. Caches derived models/plans so requests do not repeat compilation work.

### 1.2 Optional AOT mode (mapping packs)

In AOT mode, a separate build/CLI step produces a **Mapping Pack**:

- a single binary blob (protobuf payload compressed with zstd),
- containing the compiled relational mapping artifacts for a specific `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`,
- distributed as a file (primary option) and loaded by DMS on demand.

At runtime, after a request is routed to a database instance and DMS reads that database’s recorded `EffectiveSchemaHash`, DMS:

- selects the matching mapping pack (or runtime compilation fallback),
- loads/caches it, and
- proceeds with schema-dependent work (flattening/reconstitution, SQL generation, etc).

---

## 2. Goals and non-goals

### 2.1 Goals

- **Keyed by `EffectiveSchemaHash`**: mapping packs are selected strictly by the database’s recorded hash.
- **Support multi-instance deployments**: a single server can serve many databases with different effective schemas.
- **Reduce cold-start CPU and latency**: avoid compiling models/plans under production traffic.
- **Stable, redistributable artifact**: packs can be built in CI and shipped with deployments.
- **Compatibility checks**: the consumer must reject packs that do not match its expected pack format and mapping version.

### 2.2 Non-goals (for this document)

- Full multi-`ApiSchema.json` multi-instance serving (validation/query semantics per DB) is a **future** step.
  - This document focuses on the **JSON→relational mapping compilation** layer (shape model + plans).
- Redis and database-resident distribution are future options

---

## 3. Terminology

- **Effective schema**: the *core + extension* `ApiSchema.json` set as it affects relational mapping.
- **`EffectiveSchemaHash`**: deterministic SHA-256 fingerprint of the effective schema set and mapping-version constants (see `data-model.md`).
- **Dialect**: the target SQL engine (e.g., PostgreSQL vs SQL Server).
  - Target platforms: the latest generally-available (GA) non-cloud releases of PostgreSQL and SQL Server.
- **Relational mapping version**: a DMS-controlled constant that forces a mismatch when mapping rules change even if `ApiSchema.json` content is unchanged.
- **Mapping pack**: a redistributable artifact containing precompiled mapping objects for a single effective schema hash (and dialect).
- **Pack format version**: a binary format/protocol version for the mapping pack serialization itself.
  - Purpose: allow the DMS consumer to detect “I do/do not know how to decode this pack” independent of `EffectiveSchemaHash`.
  - Bump this only for **breaking changes** to the envelope/payload representation (e.g., incompatible protobuf schema changes, field renames/removals, semantic changes to how plans are encoded, or changes to the compression/encryption/signing envelope behavior).
  - Do not use this for schema/mapping evolution: those are handled by `EffectiveSchemaHash` and the relational mapping version.

---

## 4. Where this fits in request processing

This document assumes the per-database schema fingerprint selection described in `transactions-and-concurrency.md`:

1. Request arrives
2. DMS routes to a `DmsInstance` → connection string
3. DMS reads the database’s `EffectiveSchemaHash` (cached per connection string)
4. **NEW (AOT option)**: DMS ensures a matching mapping set is available:
   - load pack from file if present, otherwise compile at runtime (if allowed)
5. Schema-dependent work begins:
   - plan lookup, flattening, SQL execution, reconstitution

Design invariant:
- **No schema-dependent work happens before the effective hash is resolved and the mapping set is selected.**

---

## 5. What we precompute

The pack is the ahead-of-time equivalent of the plan compilation/caching described in `flattening-reconstitution.md`.

Pack contains:
- the deterministic `dms.ResourceKey` seed mapping for this effective schema:
  - ordered list of `(ResourceKeyId, ProjectName, ResourceName, ResourceVersion)` entries (ids are `smallint`, typically 1..N),
  - used by DMS runtime to validate `dms.ResourceKey` and to translate between `ResourceKeyId` (stored in core tables) and `QualifiedResourceName` (plan cache key),
- per-resource compiled plans:
  - `ResourceWritePlan` (including `TableWritePlan` SQL and bindings)
  - `ResourceReadPlan` (including `TableReadPlan` SQL and bindings)
  - `IdentityProjectionPlan` entries required by reconstitution
- any additional metadata needed to execute those plans without re-deriving/compiling from `ApiSchema.json`.

Logical plan pack identity is `(EffectiveSchemaHash, Dialect, RelationalMappingVersion, PackFormatVersion)`.

For file distribution, the lookup key is typically `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)` (directory + filename), and `PackFormatVersion` is validated from the envelope after reading.

### Determinism scope (`.mpack` files)

Mapping pack determinism is defined at the mapping-set level, not as byte-for-byte file identity:

- **Required (PackFormatVersion=1)**: byte-for-byte stable **payload bytes** (deterministic protobuf serialization + deterministic ordering), and stable SQL text, for a fixed `(EffectiveSchemaHash, dialect, relational mapping version, pack format version)`.
- **Not required**: byte-for-byte identical `.mpack` file contents (envelope metadata and compression can vary).

Recommended equivalence check:
- validate keying fields from the envelope,
- decompress and parse the payload,
- (if present) validate `payload_sha256`,
- compare the parsed payload content (or a deterministic fingerprint of it).

### Deterministic ordering (pack reproducibility)

Pack builders must emit repeated fields and nested plan/model structures in a deterministic order so that a given `(EffectiveSchemaHash, Dialect, RelationalMappingVersion, PackFormatVersion)` produces reproducible payload semantics.

Recommended ordering rules: see `reference/design/backend-redesign/ddl-generation.md` (“Deterministic output ordering (DDL + packs)”).

Minimum requirements:
- `resource_keys`: ordered by `resource_key_id` ascending.
- `resources`: ordered by `(project_name, resource_name)` using ordinal string ordering.
- Any per-resource lists (tables, columns, constraints, indexes, views) follow the same deterministic ordering rules used by the DDL generator.

### SQL text canonicalization (pack reproducibility)

Mapping packs embed compiled SQL strings (read/write/projection plans). For reproducible packs and stable golden-file tests, pack builders MUST ensure that compiled SQL text is canonicalized:

- stable whitespace/formatting (`\n` line endings, stable indentation, no trailing whitespace, stable keyword casing),
- stable alias naming, and
- stable parameter naming derived deterministically from the compiled model/bindings.

These requirements are normative for both runtime compilation and AOT pack compilation (see `reference/design/backend-redesign/flattening-reconstitution.md` “SQL text canonicalization (required)”).

---

## 6. Centralized protobuf code generation (“contracts package”)

We use compressed Protobuf as our mapping pack format. C# Protobuf code requires a codegen step after defining the pack format schema. To avoid checked-in generated code in the main DMS repo and avoid requiring `protoc` at every build of every consuming project:

- Create a dedicated **contracts project** that owns:
  - the `.proto` schema for mapping packs,
  - generated C# code (via `Grpc.Tools`), and
  - packaging as a NuGet package.

Suggested packages:
- `EdFi.DataManagementService.MappingPacks.Contracts` (C# generated types)

Recommended starting point:
- Keep the contracts project **in this repository** (so producer/consumer can evolve together), and optionally publish it as a NuGet package when other repos need to read/write packs.

All pack producers/consumers MUST reference the same contracts package (or project) version so they agree on tags and wire format.

Build policy:
- Never reuse field numbers.
- Add new fields as optional with new numbers.
- Retain deprecated fields to preserve backwards compatibility where possible.

---

## 7. Pack file distribution

### 7.1 File naming and layout

Goal: allow DMS to locate candidate packs cheaply and deterministically.

Recommended layout (one file per hash + dialect):

```
{MappingPackRoot}/
  pgsql/
    dms-mappingpack-{relMappingVersion}-{effectiveSchemaHash}.mpack
  mssql/
    dms-mappingpack-{relMappingVersion}-{effectiveSchemaHash}.mpack
```

Notes:
- `effectiveSchemaHash` is lowercase hex (64 chars).
- `relMappingVersion` is a short constant string (e.g., `v1`).
- The file contains an embedded header with the same values; file naming is not trusted as the source of truth.

### 7.2 DMS configuration

Suggested configuration surface:

```json
{
  "MappingPacks": {
    "Enabled": true,
    "Required": false,
    "RootPath": "/etc/dms/mapping-packs",
    "CacheMode": "InMemory",
    "AllowRuntimeCompileFallback": true
  }
}
```

Semantics:
- `Enabled=false`: ignore pack loading; always compile at runtime.
- `Enabled=true` + `AllowRuntimeCompileFallback=true`: prefer pack, fallback to runtime compile if missing.
- `Enabled=true` + `Required=true` (or `AllowRuntimeCompileFallback=false`): reject requests for DBs whose hash has no pack available.

### 7.3 Load strategy (lazy, per effective hash)

DMS should not eagerly load all packs at startup in multi-instance deployments.

Recommended:
- Index packs by file name (directory scan) at startup or first request.
- Load/validate/decompress/deserialize only when a request targets a DB hash that is not already cached.

Concurrency:
- Ensure only one thread loads a given pack at a time (use `ConcurrentDictionary<MappingPackKey, Lazy<Task<...>>>`).

---

## 8. Compression

Packs are always compressed with **Zstandard (zstd)**.

Why:
- File packs can be tens of MB (SQL strings + per-resource plans).
- Compression reduces disk I/O, container image size, and Redis memory if/when used.
- zstd decompression is extremely fast, compression is very small

Implementation approach:
- The pack header records the **uncompressed payload length**.
- The consumer always **decompresses with zstd**

---

## 9. Wire format and envelopes

Normative PackFormatVersion=1 `.mpack` bytes (envelope/payload schema + validation requirements) are defined in:

- `reference/design/backend-redesign/mpack-format-v1.md`

### 9.1 Keying fields that must be embedded

The pack must embed at least:

- `effective_schema_hash` (string)
- `dialect` (enum)
- `relational_mapping_version` (string)
- `pack_format_version` (int)

`pack_format_version` exists so the consumer can reject packs it cannot decode even if the `EffectiveSchemaHash` matches; it should be treated as a strict protocol/version gate and only bumped for breaking serialization/envelope changes.

### 9.2 Suggested protobuf schema (high level)

The contracts package would define a schema like (high level only; see the normative `.proto` in `reference/design/backend-redesign/mpack-format-v1.md`):

```proto
syntax = "proto3";

package edfi.dms.mappingpacks.v1;

enum SqlDialect {
  SQL_DIALECT_UNSPECIFIED = 0;
  SQL_DIALECT_PGSQL = 1;
  SQL_DIALECT_MSSQL = 2;
}

message MappingPackEnvelope {
  // Self-identifying header
  string effective_schema_hash = 1;
  SqlDialect dialect = 2;
  string relational_mapping_version = 3;
  uint32 pack_format_version = 4;

  // Payload is always MappingPackPayload encoded as protobuf, then zstd-compressed.
  uint64 zstd_uncompressed_payload_length = 5;

  // Zstd-compressed bytes of MappingPackPayload
  bytes payload_zstd = 10;
}

message MappingPackPayload {
  // The payload schema can evolve independently, but should remain compatible.
  repeated ResourcePack resources = 1;
  repeated ResourceKeyEntry resource_keys = 2;
}

message ResourceKeyEntry {
  // Deterministic id (seeded by DDL generator) used in core tables.
  uint32 resource_key_id = 1; // must fit in SQL smallint
  string project_name = 2;
  string resource_name = 3;
  string resource_version = 4; // SemVer from ApiSchema projectSchema.projectVersion
}

message ResourcePack {
  string project_name = 1;
  string resource_name = 2;

  // Plan packs always include dialect-specific compiled plans.
  ResourcePlans plans = 10;
}

message ResourcePlans {
  // Omitted: compiled SQL strings and binding metadata for write/read/projection.
}
```

Notes:
- The **envelope** is uncompressed protobuf.
- The envelope’s `payload_zstd` is a second protobuf message (`MappingPackPayload`) encoded as protobuf and then zstd-compressed.
- This allows reading the keying fields without decompressing the payload.

---

## 10. Producer: pack builder CLI

### 10.1 Recommended tool shape

A CLI utility (can be a new executable or a mode of the DDL generator) that:

- loads the effective `ApiSchema.json` set,
- computes `EffectiveSchemaHash` (same algorithm as `data-model.md`),
- derives `RelationalResourceModel` per resource,
- compiles dialect-specific plans,
- serializes to protobuf payload,
- zstd-compresses it,
- writes the `.mpack` file.

Suggested CLI:

```
dms-schema pack build \
  --dialect pgsql \
  --apiSchemaPath ./ApiSchema \
  --out ./mapping-packs/pgsql \
  --relMappingVersion v1
```

Notes:
- A single CLI with subcommands is recommended because DDL generation and pack building share the same compilation pipeline (schema load/merge, hashing, derived model, dialect SQL compilation).
- Alternate layout (equivalent): split `dms-schema` into separate executables (DDL/provisioning vs packs), but require they reference the same underlying compilation libraries and constants.

### 10.2 Example producer code (sketch)

```csharp
public sealed class MappingPackBuilder
{
    public async Task BuildAsync(BuildOptions options, CancellationToken ct)
    {
        ApiSchemaDocuments docs = LoadAndMergeApiSchema(options.ApiSchemaPath);
        string effectiveSchemaHash = EffectiveSchemaHashCalculator.Compute(docs, options.RelMappingVersion);

        MappingPackPayload payload = new();

        foreach (var resource in docs.GetAllResources())
        {
            RelationalResourceModel model = RelationalResourceModelBuilder.Build(resource.Schema);
            ResourcePlans plans = RelationalPlanCompiler.CompileAllPlans(model, options.Dialect, docs);

            payload.Resources.Add(new ResourcePack
            {
                ProjectName = resource.ProjectName,
                ResourceName = resource.ResourceName,
                Plans = SerializePlans(plans),
            });
        }

        byte[] payloadBytes = payload.ToByteArray(); // generated by contracts package
        byte[] compressed = CompressZstd(payloadBytes);

        byte[] sha = SHA256.HashData(payloadBytes);

        MappingPackEnvelope envelope = new()
        {
            EffectiveSchemaHash = effectiveSchemaHash,
            Dialect = options.Dialect,
            RelationalMappingVersion = options.RelMappingVersion,
            PackFormatVersion = MappingPackFormat.V1,
            ZstdUncompressedPayloadLength = (ulong)payloadBytes.Length,
            PayloadSha256 = ByteString.CopyFrom(sha),
            Producer = "dms-mappingpack",
            ProducerVersion = options.ProducerVersion,
            PayloadZstd = ByteString.CopyFrom(compressed),
        };

        string fileName = $"dms-mappingpack-{options.RelMappingVersion}-{effectiveSchemaHash}.mpack";
        await File.WriteAllBytesAsync(Path.Combine(options.OutDir, fileName), envelope.ToByteArray(), ct);
    }
}
```

---

## 11. Consumer: DMS mapping pack loader and caches

### 11.1 Cache keys

At runtime, DMS needs two related caches:

1) **Connection string → EffectiveSchemaHash**
- cached per connection string (first DB use)

2) **EffectiveSchemaHash(+ dialect) → mapping set**
- cached per effective hash (pack or compiled)

Recommended key shape:

```csharp
public readonly record struct MappingPackKey(
    string EffectiveSchemaHash,
    SqlDialect Dialect,
    string RelationalMappingVersion,
    uint PackFormatVersion);
```

### 11.2 Interfaces

Suggested separation:

```csharp
public interface IMappingSetProvider
{
    Task<IMappingSet> GetOrCreateAsync(MappingPackKey key, CancellationToken ct);
}

public interface IMappingPackStore
{
    // Returns null if the pack is not present in this store.
    Task<MappingPackEnvelope?> TryGetAsync(MappingPackKey key, CancellationToken ct);
}
```

Where:
- `IMappingPackStore` has multiple implementations (file now; Redis/DB later).
- `IMappingSetProvider` coordinates loading + fallback runtime compilation + in-memory caching.

### 11.3 File-based store (sketch)

```csharp
public sealed class FileMappingPackStore : IMappingPackStore
{
    private readonly string _rootPath;

    public FileMappingPackStore(string rootPath) => _rootPath = rootPath;

    public async Task<MappingPackEnvelope?> TryGetAsync(MappingPackKey key, CancellationToken ct)
    {
        string dialectDir = key.Dialect switch
        {
            SqlDialect.SqlDialectPgsql => "pgsql",
            SqlDialect.SqlDialectMssql => "mssql",
            _ => throw new InvalidOperationException("Unsupported dialect")
        };

        string fileName =
            $"dms-mappingpack-{key.RelationalMappingVersion}-{key.EffectiveSchemaHash}.mpack";

        string path = Path.Combine(_rootPath, dialectDir, fileName);
        if (!File.Exists(path)) return null;

        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        return MappingPackEnvelope.Parser.ParseFrom(bytes);
    }
}
```

### 11.4 Envelope validation + decompression (sketch)

```csharp
public static class MappingPackLoader
{
    public static MappingPackPayload LoadPayload(MappingPackEnvelope env, MappingPackKey expectedKey)
    {
        if (!string.Equals(env.EffectiveSchemaHash, expectedKey.EffectiveSchemaHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Pack effective hash mismatch");

        if (env.Dialect != expectedKey.Dialect)
            throw new InvalidOperationException("Pack dialect mismatch");

        if (!string.Equals(env.RelationalMappingVersion, expectedKey.RelationalMappingVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Pack mapping version mismatch");

        if (env.PackFormatVersion != expectedKey.PackFormatVersion)
            throw new InvalidOperationException("Pack format version mismatch");

        byte[] compressed = env.PayloadZstd.ToByteArray();
        byte[] payloadBytes = DecompressZstd(compressed, (long)env.ZstdUncompressedPayloadLength);

        if (env.PayloadSha256.Length > 0)
        {
            byte[] actual = SHA256.HashData(payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(actual, env.PayloadSha256.ToByteArray()))
                throw new InvalidOperationException("Pack payload checksum mismatch");
        }

        return MappingPackPayload.Parser.ParseFrom(payloadBytes);
    }

    private static byte[] DecompressZstd(byte[] bytes, long uncompressedLength)
    {
        // Use a zstd implementation such as ZstdSharp, ZstdNet, or an internal wrapper.
        // The API shown here is illustrative.
        return Zstd.Decompress(bytes, uncompressedLength);
    }
}
```

### 11.5 `dms.ResourceKey` validation (required)

After loading a mapping set for a database, DMS should validate that the database’s seeded `dms.ResourceKey` mapping matches the mapping set (fail fast on mismatch).

Recommended validation (once per database/connection string, cached):
- Fast path: compare a stored seed fingerprint without reading the full table:
  1. Read: `SELECT ResourceKeyCount, ResourceKeySeedHash FROM dms.EffectiveSchema WHERE EffectiveSchemaSingletonId = 1`.
  2. Compare to the expected `(ResourceKeyCount, ResourceKeySeedHash)` derived from the mapping set’s embedded `resource_keys` list (same canonicalization as the DDL generator).
- Slow path (diagnostics on mismatch):
  1. Read: `SELECT ResourceKeyId, ProjectName, ResourceName, ResourceVersion FROM dms.ResourceKey ORDER BY ResourceKeyId;`
  2. Diff vs. `payload.resource_keys` and fail fast with a detailed mismatch error.

After validation, build and cache:
   - `QualifiedResourceName -> ResourceKeyId` (for writes / change-query params)
   - `ResourceKeyId -> (QualifiedResourceName, ResourceVersion)` (for background tasks, diagnostics, and materializing denormalized metadata)

### 11.6 Mapping set provider (single-flight load + fallback)

```csharp
public sealed class MappingSetProvider : IMappingSetProvider
{
    private readonly IMappingPackStore _store;
    private readonly IRuntimeCompiler _runtimeCompiler;
    private readonly ConcurrentDictionary<MappingPackKey, Lazy<Task<IMappingSet>>> _cache = new();

    public MappingSetProvider(IMappingPackStore store, IRuntimeCompiler runtimeCompiler)
    {
        _store = store;
        _runtimeCompiler = runtimeCompiler;
    }

    public Task<IMappingSet> GetOrCreateAsync(MappingPackKey key, CancellationToken ct)
    {
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<IMappingSet>>(() => LoadOrCompileAsync(k, ct)));
        return lazy.Value;
    }

    private async Task<IMappingSet> LoadOrCompileAsync(MappingPackKey key, CancellationToken ct)
    {
        var env = await _store.TryGetAsync(key, ct);
        if (env != null)
        {
            var payload = MappingPackLoader.LoadPayload(env, key);
            return MappingSet.FromPayload(payload);
        }

        // Optional fallback: compile at runtime if allowed by config.
        return await _runtimeCompiler.CompileAsync(key, ct);
    }
}
```

---

## 12. Future distribution options

### 12.1 Redis warm store

Store the same `.mpack` bytes in Redis, keyed by:

```
dms:mappingpack:{dialect}:{relMappingVersion}:{packFormatVersion}:{effectiveSchemaHash}
```

Operational model:
- CI/pack builder publishes packs to Redis.
- DMS reads-through cache on first use of a DB hash.

This is a *distribution mechanism* only; it does not change the pack format.

### 12.2 Database-resident packs

Store the same `.mpack` bytes in a table in the target DB:

```
dms.CompiledMappingPack(
  EffectiveSchemaHash varchar(64) not null,
  Dialect varchar(16) not null,
  RelationalMappingVersion varchar(32) not null,
  PackFormatVersion int not null,
  PackBlob bytea not null,
  CreatedAt timestamp with time zone not null default now(),
  primary key (EffectiveSchemaHash, Dialect, RelationalMappingVersion, PackFormatVersion)
)
```

Operational model:
- DDL tool inserts/updates this row when provisioning/upgrading the database.
- DMS reads the pack from the target database on first use (self-contained DB).

Again: this is only a storage mechanism; the `.mpack` bytes are unchanged.
