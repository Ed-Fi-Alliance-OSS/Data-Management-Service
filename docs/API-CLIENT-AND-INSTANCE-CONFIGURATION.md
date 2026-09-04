# API Client and Data Store Configuration

## Overview

The Ed-Fi API Configuration Service manages API client
credentials and data store routing through a dedicated configuration database.
This database stores vendor and application information, API client credentials,
data store connection strings, and route context mappings for multi-tenant
deployments.

All configuration data resides in the `dmscs` (DMS Configuration Service) schema
within the configuration database.

## Prerelease Rename Note

DMS-1198 renames the prerelease CMS management contract and database objects
from DMS instance terminology to data store terminology. Deployments upgrading
from alpha builds that already created `DmsInstance*` CMS tables should recreate
the CMS configuration database, or run an operator-managed migration that copies
the old `DmsInstance*` rows into the new `DataStore*` tables before using the
new CMS API routes. The DMS-1198 branch does not provide an in-place migration
from the old prerelease CMS table names.

External CMS callers must update from the old DMS instance route and JSON field
names to the `dataStore*` routes and `dataStore*` payload fields.

## Ed-Fi DMS Data Stores

### Data Store Storage and Security

The connection strings for data stores are configured in the DMS Configuration
Service database and stored in the `DataStore` table. For security purposes,
connection strings are encrypted using AES encryption to protect database
credentials from unauthorized access.

### API Client to Data Store Association

Each API client can be associated with one or more data stores. In the
simplest case, each API client has access to a single instance, providing a
streamlined experience where the client uses a fixed API base URL (e.g.,
`http://localhost:8080/data/ed-fi/students`).

### Context-Based Routing

Alternatively, the DMS supports **context-based routing**, which allows a
single API client to access multiple data stores by including route
qualifiers in the request URL. This approach combines API client/data store
associations with route context values to determine which database should
handle each request.

When context-based routing is enabled, route qualifiers are included in the
API path (e.g., `http://localhost:8080/255901/2024/data/ed-fi/students`),
where `255901` and `2024` represent contextual values such as district ID and
school year.

The `DataStoreContext` table stores the context key-value pairs for
each data store, enabling the DMS API to match incoming route qualifiers
against configured data stores.

### Related Tables

```mermaid
erDiagram
    Application ||--o{ ApiClient : "has"
    ApiClient ||--o{ ApiClientDataStore : "can access"
    DataStore ||--o{ ApiClientDataStore : "accessible by"
    DataStore ||--o{ DataStoreContext : "has"
    DataStore ||--o{ DataStoreDerivative : "has"

    Application {
        int Id PK
        varchar ApplicationName
        int VendorId FK
        varchar ClaimSetName
    }

    ApiClient {
        int Id PK
        int ApplicationId FK
        varchar ClientId
        uuid ClientUuid
    }

    ApiClientDataStore {
        int ApiClientId PK_FK
        int DataStoreId PK_FK
    }

    DataStore {
        int Id PK
        varchar DataStoreType
        varchar Name
        bytea ConnectionString
    }

    DataStoreContext {
        int Id PK
        int DataStoreId FK
        varchar ContextKey
        varchar ContextValue
    }

    DataStoreDerivative {
        int Id PK
        int DataStoreId FK
        varchar DerivativeType
        bytea ConnectionString
    }
```

#### DataStore

Stores data store definitions and encrypted connection strings.

| Column | Type | Description |
|--------|------|-------------|
| Id | INT | Primary key |
| DataStoreType | VARCHAR(50) | Data store classification |
| Name | VARCHAR(256) | Human-readable data store name |
| ConnectionString | BYTEA | Encrypted database connection string |

#### DataStoreContext

Stores context key-value pairs for route-based data store resolution.

| Column | Type | Description |
|--------|------|-------------|
| Id | INT | Primary key |
| DataStoreId | INT | Foreign key to DataStore |
| ContextKey | VARCHAR(256) | Context dimension name |
| ContextValue | VARCHAR(256) | Context value |

**Constraint:** `UNIQUE (DataStoreId, ContextKey)` ensures each data store has
only one value per context key.

#### DataStoreDerivative

Stores derivative data stores (read replicas and snapshots) associated with a parent data store.

| Column | Type | Description |
|--------|------|-------------|
| Id | INT | Primary key |
| DataStoreId | INT | Foreign key to parent DataStore |
| DerivativeType | VARCHAR(50) | Type of derivative: "ReadReplica" or "Snapshot" |
| ConnectionString | BYTEA | Encrypted database connection string |

**Foreign Key:** CASCADE DELETE on `DataStoreId` - when a parent DataStore is
deleted, all its derivative data stores are automatically deleted.

**Constraint:** `UNIQUE (DataStoreId, DerivativeType)` ensures each data store
has at most one ReadReplica and at most one Snapshot. Its backing index leads
with DataStoreId, so it also serves lookups by parent data store and the
child-side foreign-key maintenance.

**Constraint:** a check constraint restricts `DerivativeType` to exactly
"ReadReplica" or "Snapshot", compared ordinally including length, so case and
whitespace variants are rejected in both engines.

#### ApiClient

Stores OAuth client credentials for applications.

| Column | Type | Description |
|--------|------|-------------|
| Id | INT | Primary key |
| ApplicationId | INT | Foreign key to Application |
| ClientId | VARCHAR(36) | OAuth client identifier |
| ClientUuid | UUID | Globally unique client identifier |

#### ApiClientDataStore

Maps API clients to data stores they can access (many-to-many).

| Column | Type | Description |
|--------|------|-------------|
| ApiClientId | INT | Foreign key to ApiClient |
| DataStoreId | INT | Foreign key to DataStore |

## Data Store Derivatives

Data Store Derivatives are alternate database instances associated with a parent
data store, such as read replicas or snapshots. Read replicas distribute query
load, while snapshots preserve point-in-time data for backup, testing, or analysis.

A `Snapshot` derivative must reference a database that is frozen for the duration
of a client's paging session. Change-version-filtered collection reads served
from a snapshot are paged in change-version order, and that ordering is only safe
over data that cannot change while it is being walked. Configuring a source that
continues to apply changes — a live secondary, or a standby still shipping logs —
as a `Snapshot` rather than a `ReadReplica` can silently return one document
twice and skip another within a single walk, with no error reported. A
`ReadReplica` carries no such requirement: reads served from one keep the live
paging rule. See [Cursor Paging](./CURSOR-PAGING.md).

The requirement covers the derivative's identity as well as the database behind
it. While reads against a snapshot are in progress, do not re-point the
derivative at a different connection string, and do not re-create the database
behind an unchanged one. Neither is reported: the pages already returned are not
re-read, and the reads finish normally with their pages drawn from two different
points in time. Re-creating the database at an unchanged connection string is the
more severe of the two, because no configuration changed and nothing keyed on the
connection string can observe the substitution. Removing the derivative row, or
dropping the snapshot or otherwise making it unreachable, is the one case that
does surface — as `404` with `Snapshot not found.` — interrupting the reads
rather than silently answering them from a different copy.

Each derivative type is stored with its own encrypted connection string and is
automatically deleted when its parent data store is removed (CASCADE DELETE).

### Configuration

Data store and route context configuration is managed through the DMS
Configuration Service REST API. See the
[Database segmentation documentation](DATABASE-SEGMENTATION-STRATEGY.md) for detailed
configuration examples and usage patterns.
