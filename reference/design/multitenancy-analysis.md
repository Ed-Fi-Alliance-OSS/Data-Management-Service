# Multi-Tenancy Data Segregation Analysis

> [!IMPORTANT]
> Plans for utilizing OpenSearch/Elasticsearch to support the API's read load
> have been dropped. The OpenSearch sections below are preserved for historical
> context. The database and Kafka analysis and design direction remain relevant.

## Introduction

Adding support for multitenant deployments introduces new privacy concerns as it
pertains to physical storage of sensitive student data that is subject to FERPA
regulations. The goal of this analysis was to identify and document the options
available for handling multitenant data storage.

## Data Segregation Analysis

The DMS solution consists of the following architectural components that
physically store sensitive data:

* PostgreSQL databases for primary data storage
* Kafka storage for streaming data
* OpenSearch for serving GET requests from the API (dropped — see note above)

Each of these components have distinct characteristics and approaches that would
be needed to achieve various levels of logical and physical data segregation
between tenants.

> [!NOTE]
> In many discussions about multitenancy, the term tenant is used to define the
> boundaries for data segregation. However, in the Ed-Fi API solutions, a tenant
> refers to the scope of administrative control for managing multiple ODS/DMS
> _instances_. Each API client is associated with a specific ODS/DMS instance
> and should not be able to see data from other ODS/DMS instances, even though
> they may be provisioned by the same tenant. Thus, the focus of this document
> is around data segregation at the _instance_ level.

## Database (PostgreSQL or SQL Server)

With relational databases, the data segregation options are well documented and
widely understood due to the maturity of the technology. The primary options are
as follows.

### Row-level data segregation (low isolation)

Every table in the database would have an "instance id" column that is then used
by the application to filter data for each request. Alternatively, the database
engine may support row-level security but this would have other implications on
connection management and pooling that probably make it impractical. It is only
being mentioned here for completeness because while it is technically possible,
it is deemed to present higher risk for the Ed-Fi Alliance given privacy concerns
around student data and FERPA regulations.

### Schema-level data segregation (medium isolation)

Every instance would have a separate _schema_ in a shared DMS database. While
serving to reduce the number of databases needed to host multiple instances and
thereby reducing costs for hosting models with an incremental per-database cost,
it would come with extra management and support complexity as compared with
row-level or database-level approaches. Schema-based segregation does offer less
risk of data exposure than row-level filtering as an "unfiltered" query is likely
to fail due to an invalid schema rather than expose other instances' data, but in
practice it is not seen as a scalable or supportable strategy. Mentioned here
only for completeness rather than as a recommended option.

### Database-level segregation (high isolation)

Every instance would have its own database. Databases can be created and deleted
easily per tenant instance, providing the flexibility needed for horizontal
scalability. This also provides the highest level of data segregation but could
result in higher costs if the hosting model has an associated per-database cost.

### Hybrid Approach

These three approaches are not mutually exclusive. For example, one could use
both schema-level or row-level segregation along with database-level segregation
to manage a large implementation. A very large school district would likely be
allocated a dedicated database, while many very small districts could be combined
into a single database.

The API software could support such flexible deployment and management options
with a design that incorporates the following:

* Each API client is associated with a specific instance.
* Each instance has an associated configured connection string to a specific DMS
  database.
* The API implementation incorporates instance-based schemas into all queries as
  appropriate for the API client while processing requests.

## Kafka

Kafka plays a central role in the DMS architecture as a durable, high-throughput
backbone for streaming data between components. However, Kafka was not originally
designed for strict multitenancy or per-tenant isolation and supporting high
levels of instance-level data segregation in Kafka requires careful
consideration. The following approaches were evaluated.

### Cluster-per-instance segregation (least feasible)

Running a dedicated Kafka cluster per instance would theoretically provide
complete physical isolation of data streams. However, this is entirely
impractical for a large scale (e.g. 1300 instances for Texas). Kafka clusters
are complex to deploy and manage, and the operational overhead of managing
thousands of brokers, Zookeeper (or KRaft) nodes, and associated monitoring
infrastructure is quite impractical. This approach is excluded from serious
consideration.

### Topic-per-instance segregation (recommended)

This approach offers strong isolation between instances and aligns well with data
separation requirements related to FERPA. It simplifies ACL configuration,
consumer group management, and auditing, since each instance's data is fully
separated at the topic level. This provides clear instance-based isolation at the
Change Data Capture (CDC) level for 3rd party consumers.

### Shared topic with tenant/instance filtering (least desirable)

An alternative model would consolidate messages for all instances into a single
shared topic and rely on an instance identifier field in the message payload to
filter and route messages. While this reduces topic count and centralizes stream
processing, it increases the risk of cross-instance data leakage due to
misconfigured consumers or processing bugs. Given the sensitivity of student data
and FERPA compliance goals, this approach is not recommended.

## Kafka Connect

The choice of relational database engine (PostgreSQL or SQL Server) and the
database multitenancy strategy both have a strong influence on how Debezium and
Kafka Connect must be configured to accurately capture and route
tenant/instance-specific data.

For PostgreSQL, the Debezium connector uses a replication slot that is scoped to
a single database and cannot span multiple databases.

In contrast, Debezium's integration with SQL Server does not have the same
connector-per-database limitation and can be configured to process changes from
multiple databases on the same server.

The primary scaling challenges are at the Kafka Connect cluster level — worker
capacity, scaling, and fault tolerance — rather than in the number of connectors.

## OpenSearch (Historical — Support Dropped)

> [!IMPORTANT]
> This section is preserved for historical context. OpenSearch/Elasticsearch
> support as a read store has been dropped.

OpenSearch presents unique challenges for supporting multitenancy at the scale
required by a large SEA (e.g. Texas). The following data segregation strategies
were evaluated.

### Cluster-level segregation (least feasible)

A dedicated OpenSearch cluster per instance would ensure full isolation but is
prohibitively expensive and operationally complex at 1300+ instances.

### Index-per-instance segregation (anti-pattern)

The current implementation uses an index-per-resource strategy (~385 indexes).
Extrapolating to 1300 instances would result in ~500,000 indexes — a known
architectural anti-pattern that degrades cluster performance and stability.

See [OpenSearch Cluster State](https://opster.com/guides/opensearch/opensearch-capacity-planning/opensearch-cluster-state/)
and [Multi-Tenancy with Elasticsearch and OpenSearch](https://bigdataboutique.com/blog/multi-tenancy-with-elasticsearch-and-opensearch-c1047b).

### Shared indexes with filtered aliases (most feasible, but dropped)

The data for each instance would be accessed through _filtered aliases_ in
OpenSearch, enforcing instance-level access constraints via query-time filtering.
While the most viable option at scale, the deep offset-based pagination
limitation ultimately led to dropping OpenSearch support entirely.

### Concerns Regarding Index-Per-Resource Approach

The current DMS implementation creates an index for each resource. The current
Ed-Fi model has 200+ descriptors resulting in many very small indexes, which is
an anti-pattern. A more scalable solution would consolidate data into a smaller
number of shared indexes per domain or resource type.

## Design Direction

* **Database (Hybrid Approach)** — Implement support for multiple instances
  through multiple DMS databases. Associate each API client with a specific
  instance and use the instance-specific connection string to connect to the
  appropriate DMS database.
* **Debezium/Kafka (Topic-per-Instance Segregation)** — Publish changes into
  instance-based topics to make it feasible to implement appropriate
  authorization guardrails for 3rd party Kafka consumers.
* **OpenSearch** — Dropped. Cannot support deep offset-based pagination.
  Kafka support remains as an optional feature for hosts with streaming use cases.
