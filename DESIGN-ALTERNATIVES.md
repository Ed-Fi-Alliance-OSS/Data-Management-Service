Review the design of the DMS, in particular the original design document /home/brad/work/dms-root/Project-Tanager/docs/DMS/PRIMARY-DATA-STORAGE/README.md and related documents. Also review
  the actual implementation in src/dms, and in particular the SQL scripts in src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts, From this you can see the main
  purpose of DMS is to provide reference validation of resource documents, where we know the shape of each document and where the references in each document are located. We compute a hash for
  each reference and store these in the Reference and Alias tables, and use these to provide reference validation. In a traditional database system, there would be tables for each document
  type, and foreign key relationships to enforce reference validation. I want you to brainstorm alternative designs to provide reference validation for documents like these. The only constraint is that a relational database is used for document storage.

-----

## GPT-5.1-Codex-MAX-X-High

 • Looked at Project-Tanager/docs/DMS/PRIMARY-DATA-STORAGE/README.md and ALTERNATIVES.md, plus the current Postgres schema in src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/
  Deploy/Scripts/0001_Create_Document_Table.sql, 0002_Create_Alias_Table.sql, 0003_Create_Reference_Table.sql, and 0015_Create_Reference_Validation_FKs.sql. The implemented model uses
  partitioned Document, Alias, and Reference tables; reference validation is enforced by the FK from Reference to Alias (disable by dropping that FK).

  Alternative designs for reference validation while keeping relational document storage:

  - ReferentialId-as-PK: Make Documents.ReferentialId the clustered PK (partitioned), drop Alias, and have Reference store (ParentDocId, ReferencedReferentialId) with a FK to
    Documents.ReferentialId. Add a small SuperclassMap(ReferentialId, SupertypeReferentialId) to cover inheritance by inserting both IDs on write. Fewer tables/indexes and direct FK validation;
    downside is random GUID PK fragmentation and heavier updates when identities change.
  - Table-per-resource with typed FKs: Generate a table per resource with identity columns and JSON payload; reference columns are typed and FK to the target resource tables (with join
    tables for arrays). Validation is native FK on every edge; query perf is strong for typed access. Costs: schema explosion, more DDL churn when the model changes, and more migrations/index
    maintenance; inheritance needs either table-per-type hierarchy or shared supertype tables.
  - Single document table + generated reference edge table: Keep one Documents table (surrogate PK). Add a generated/stored column that extracts an array of referential IDs from
    JSON. Use a trigger or generated ReferenceEdges table (generated always as (jsonb_path_query_array(...)) stored) populated automatically on insert/update; enforce FK from
    ReferenceEdges.ReferencedReferentialId to a unique Documents.ReferentialId. No application-managed Reference writes; validation happens in-database. Tradeoffs: heavier write path in the DB,
    harder to tune partition alignment, complex JSON-path maintenance.
  - Deferred constraint trigger validation: Store references only in JSON on Documents. Add a deferred constraint trigger that, at commit, expands the document’s references
    (jsonb_array_elements) and checks existence against a unique Documents.ReferentialId (or Alias-like lookup). Fast bulk loads (constraint can be deferred or temporarily disabled) with
    centralized validation logic. Tradeoffs: validation is later in the transaction, and trigger cost grows with document fan-out.
  - Materialized lookup dimension + lightweight edges: Maintain ReferentialIdentity(ReferentialId PK, SupertypeReferentialId) table populated on document upsert. Store references as
    (ParentDocId, ReferencedReferentialId) with FK to ReferentialIdentity. This keeps Reference lean (no denormalized doc IDs) and puts inheritance logic in one place. Requires reliable dual-
    write to ReferentialIdentity; extra join to reach document metadata.
  - Async/offline validator in-RDBMS: For very high ingest, accept writes into Documents (no FK). Maintain a MissingReferences table populated by a periodic job (INSERT ... SELECT ... WHERE NOT
    EXISTS) or logical replication stream; block promotion of batches until the job is clean. Gives elastic ingest at the cost of eventual, not immediate, validation; fit only if the business
    can tolerate delayed failures.

  Each option can still use hash-based partition keys and configurable constraint toggles. Choice hinges on how strongly you need immediate FK enforcement, how much schema churn you can
  tolerate, and whether inheritance mapping is easier in an alias map or baked into the document row itself. Potential next steps: pick 1–2 candidates, prototype DDL plus a 10–50M-row ingest
  test to compare ingest cost, FK maintenance overhead, and reference-check latency. 

  -----

  ## GPT 5.1 High

  • Here’s a set of alternative designs, grounded in the current DMS approach and the docs you pointed to, but exploring different ways to get reference validation for document-shaped data in a
  relational store.

  I’ll start with a quick baseline of the current model and then walk through distinct design families with pros/cons.

  ———

  Baseline: Current DMS Design (for comparison)

  - Single Document table (dms.Document) holding JSONB plus metadata, partitioned by DocumentPartitionKey, with a surrogate Id.
  - Alias table: rows keyed by a deterministic ReferentialId (UUIDv5 hash of resource-type + natural key). Each document has one or more aliases (subclass → base-class alias). Alias links
    ReferentialId → (DocumentPartitionKey, DocumentId). Uniqueness on (ReferentialPartitionKey, ReferentialId).
  - Reference table: one row per logical reference in a document, keyed by (ParentDocumentPartitionKey, Id), storing (ParentDocumentId, AliasId, ReferentialPartitionKey,
    ReferencedDocumentPartitionKey, ReferencedDocumentId).
  - Reference validation: enforced via FK from Reference(ReferentialPartitionKey, AliasId) to Alias(ReferentialPartitionKey, Id) plus FKs back to Document, with partition-aware indexes.
  - Superclass identities (e.g., EducationOrganization satisfying a School reference) handled by inserting extra alias rows.

  The rest of the ideas below are “different ways to skin the same cat” given that:

  - You know schemas and reference locations up front.
  - Documents must live in a relational DB (often as JSON).
  - You want existence/reference validation, sometimes with inheritance semantics, at high scale.

  ———

  ### 1. Fully Normalized Resource Schema + Shadow JSON

  This is essentially “ODS-style” but with JSON stored alongside.

  - Storage
      - One table per resource type, e.g. Student, School, Section, modeled with typed columns for identities and relationships.
      - Optional JsonDocument or EdfiDoc column per row holding the original document.
      - Traditional relational modeling for inheritance (EducationOrganization base table, School table with FK to it, etc.).
  - Reference Validation
      - References are explicit FKs between resource tables (e.g., StudentSchoolAssociation.SchoolId → School.SchoolId, or via surrogate PKs).
      - Cascading updates/deletes are handled by DB’s normal FK semantics.
  - Pros
      - Very strong, familiar relational semantics; tooling and query optimizers work well.
      - Identity updates and cascading updates are “natural” where modeled.
      - Debugging and ad-hoc querying are easier than with opaque hashes.
  - Cons
      - Code-per-resource and schema-per-resource, which is exactly what DMS is trying to avoid.
      - Schema evolution is expensive; alignment with evolving Ed-Fi models is heavy.
      - Doesn’t preserve the “three generic tables” simplicity; complexity scales with resource count.

  This is mostly the baseline you explicitly contrasted against, so the remaining options aim to preserve more of DMS’s generic flavor.

  ———

  ### 2. Per-Resource Identity Tables + Generic Document Store

  Hybrid: keep a single JSON Document table, but normalize only identities per resource type.

  - Storage
      - Document(DocumentId, DocumentPartitionKey, ResourceName, EdfiDoc, …) as today (or similar).
      - For each resource type with references, an identity table, e.g.:
          - StudentIdentity(StudentUniqueId PK, DocumentPartitionKey, DocumentId FK)
          - SchoolIdentity(SchoolId PK, DocumentPartitionKey, DocumentId FK)
      - Documents insert/update logic populates the corresponding identity table based on the known natural key fields in the JSON.
  - Reference Representation & Validation
      - For each reference type, you have a “reference index” table with natural key + pointer to the parent document, e.g.:
          - StudentSchoolAssociation_SchoolRef(ParentDocumentPartitionKey, ParentDocumentId, SchoolId) with FK SchoolId → SchoolIdentity.SchoolId.
      - Insert/update on a document:
          - Parse JSON for known reference locations.
          - Insert rows into the appropriate reference index tables.
          - FK failures on those tables are your reference validation.
  - Pros
      - No hashed ReferentialId; you use natural keys directly.
      - References validated by standard FKs against per-resource identity tables.
      - Inheritance can be handled like the ODS: base identity table (EducationOrganizationIdentity) with subclass identity tables FKing back to it, and references pointing to the base.
  - Cons
      - Still requires per-resource and per-relationship tables (like query tables, but for references).
      - Composite natural keys can be wide and expensive in indexes.
      - Schema changes when references are added/removed; not as generic as the current DMS alias/reference design.

  This is a “lightweight normalized shadow graph” around a generic document store.

  ———

  ### 3. Per-Relationship “Reference Index” Tables (Join Tables Over Documents)

  Here, resource identities stay inside the JSON, but reference validation is done via join tables between document rows.

  - Storage
      - Document table as in DMS (single table with Id, PartitionKey, JSONB, etc.).
      - For each relationship (not just resource), a join table keyed by DocumentIds, e.g.:
          - StudentSchoolAssociation_StudentRef(ParentDocumentPartitionKey, ParentDocumentId, StudentDocumentPartitionKey, StudentDocumentId)
          - StudentSchoolAssociation_SchoolRef(ParentDocumentPartitionKey, ParentDocumentId, SchoolDocumentPartitionKey, SchoolDocumentId)
      - Parent document type and referenced document type are implicit in the table name.
  - Reference Validation
      - Each join table has FKs:
          - (ParentDocumentPartitionKey, ParentDocumentId) → Document(DocumentPartitionKey, Id)
          - (StudentDocumentPartitionKey, StudentDocumentId) → Document(DocumentPartitionKey, Id)
      - On insert/update of a document:
          - You first resolve references (using JSON and a lookup query on Document or auxiliary identity tables keyed by natural key).
          - Then you insert rows into the appropriate join tables.
          - FK failures in the join tables enforce reference existence.
  - Pros
      - No alias table or hashed IDs: references are directly in terms of Document PKs.
      - FKs are straightforward and type-safe; DB enforces them without extra abstraction.
      - Inheritance can be handled by ensuring that a School-typed document has a link to an EducationOrganization-typed base doc, and references target the base.
  - Cons
      - Requires a join table per reference type, which can be numerous.
      - Requires a prior step to resolve natural keys to DocumentId (you need some identity index or lookup).
      - Cascading identity changes are trickier if you want natural-key-based semantics; you may still need cascading JSON update logic (like your CASCADING-JSON doc).

  This is close in spirit to your current Reference table, but “exploded” into many type-specific tables and expressed purely in terms of doc PKs.

  ———

  ### 4. JSON + Computed/Generated Columns with FKs

  Use JSON for storage but extract reference values into computed columns and enforce FKs on those.

  - Storage
      - Document table with JSONB/JSON column.
      - For each resource type and each reference path, generated columns that pull out the referenced identity or ID:
          - Example (PostgreSQL):
            SchoolIdFromStudent AS ((EdfiDoc->'schoolReference'->>'schoolId')::int) STORED
          - In SQL Server: SchoolIdFromStudent AS JSON_VALUE(EdfiDoc, '$.schoolReference.schoolId') PERSISTED.
  - Reference Validation
      - Identity tables per resource (as in option 2), or normalized resource tables.
      - FKs defined on the generated columns:
          - FOREIGN KEY (SchoolIdFromStudent) REFERENCES SchoolIdentity(SchoolId).
      - Arrays of references can be modeled with helper tables populated via triggers (one row per array element) with generated columns on those helper tables.
  - Pros
      - Keeps a very simple “single document table” story.
      - Validation is declarative, done via FKs, and data remains easily queryable via the generated columns.
      - No explicit alias or reference table required for simple scalar references.
  - Cons
      - Not generic: you need to add computed columns per reference path per resource.
      - Arrays and complex nested references need side tables anyway.
      - Portability is harder: JSON expression support and generated-column + FK support differ between SQL Server and PostgreSQL.
      - Superclass identity semantics (e.g., School vs EducationOrganization) still require either inheritance tables or alias-like patterns.

  This is attractive if you are willing to let the DB schema mirror the API schema more closely and accept DB-specific JSON features.

  ———

  ### 5. Pure JSON + Trigger-Based Validation Using Expression Indexes

  Keep only a documents table and use triggers/procs to enforce reference existence.

  - Storage
      - Single Document table with JSON field and metadata, partitioned as needed.
      - No alias or reference tables.
  - Reference Validation
      - For each resource type with references, define:
          - Expression indexes on paths used as identities (e.g., EdfiDoc->'schoolId' for School, or appropriate JSON paths).
      - Implement BEFORE INSERT/UPDATE triggers per resource family:
          - Parse references from JSON using paths known from the API schema.
          - For each reference, run a lookup against the appropriate identity expression index (SELECT 1 FROM Document WHERE ResourceName = 'School' AND EdfiDoc->'schoolId' = ?).
          - Raise an error if any lookup is missing.
      - Optional: triggers on delete to prevent deleting referenced documents unless cascading behavior is implemented.
  - Pros
      - Schema stays very generic (one table, per-resource triggers only).
      - No alias hashes, reference tables, or identity tables required.
      - Works even if natural keys are complex, as long as you can index the JSON expressions.
  - Cons
      - Reference validation is imperative, not declarative; correctness relies on triggers/stored procs.
      - Harder to toggle per-reference validation on/off cleanly; you’d need trigger logic or separate procs.
      - Performance depends heavily on expression indexes over JSON; more RDB engine–specific tuning.
      - Harder to get “who references this doc?” queries without a dedicated reference index table.

  This is a reasonable alternative if you prioritize schema simplicity and are willing to accept more complex DB logic.

  ———

  ### 6. Hashless Alias / Identity Design (Variant of Current Design)

  Keep the three-table structure but represent referential identity differently than a UUID hash.

  - Storage
      - Document table as today.
      - Alias table, but instead of ReferentialId UUID as a hash:
          - Add explicit columns for the identity parts: e.g., ResourceName, Namespace, Key1, Key2, … (or a structured JSON/JSONB column), plus DocumentId.
          - Unique index on (ResourceName, Key1, Key2, …) or on a canonical string representation of the identity (e.g., KeyText).
  - Reference Representation & Validation
      - Reference table similar to today, but storing either:
          - The composite identity columns directly; or
          - A surrogate integer identity key from Alias (like today’s AliasId) with FKs.
      - Instead of computing UUIDv5s, you compute a normalized key string (or multi-column key) in application code and use that to look up Alias rows.
  - Pros
      - Semantics similar to current DMS (generic three-table model, partitioning, alias-based inheritance).
      - More human-readable keys; easier to inspect and debug than opaque UUID hashes.
      - Avoids dependency on UUIDv5 and its hash characteristics; collisions are even less of a concern if using canonical strings.
  - Cons
      - Composite identity columns can become wide and multiple per resource; index size and performance may suffer.
      - You lose the nice “one opaque, fixed-size key for everything”; hashing keeps indexes small and uniform.
      - You still have alias and reference tables; complexity is similar to current design.

  This is essentially a “semantic alias” variant of the current design.

  ———

  ### 7. Normalized Inheritance Graph + Document Shadow

  Model inheritance and references in relational tables, with JSON as a payload-only shadow.

  - Storage
      - Normalized inheritance hierarchy:
          - EducationOrganization(EducationOrganizationId PK, …)
          - School(SchoolId PK, EducationOrganizationId FK, …)
          - etc.
      - A generic Document table that maps each normalized row to its JSON representation:
          - Document(ResourceName, NormalizedPkValue(s), DocumentId, DocumentPartitionKey, EdfiDoc, …)
          - Or simple DocumentId FK columns on each normalized resource table.
  - Reference Validation
      - References are FKs between normalized tables (e.g., CourseOffering.SessionId → Session.SessionId).
      - Because inheritance is explicit (School → EducationOrganization), a reference to the base can be enforced as a FK to the base.
      - DMS uses these normalized tables only for identities and relationships; all other fields live only inside the JSON payload.
  - Pros
      - Leverages full relational modeling for identity and reference validation, including inheritance.
      - JSON can be treated as a projection/serialization detail, not used for constraints.
      - Easier to support complicated inheritance semantics than with alias rows.
  - Cons
      - Very close to “ODS but with JSON shadow”; brings back per-resource tables and more complex schema management.
      - High upfront modeling cost and maintenance overhead as the Ed-Fi model changes.
      - Not generic in the sense of “no code per resource”.

  ———

  ### Putting It Together

  If you want to stay closer to DMS’s current philosophy (generic schema, partitioned document table, runtime-driven schema), but are open to alternative reference designs, the most interesting
  families are:

  - “Identity sidecar” variants (options 2, 6):
      - Keep a generic document table.
      - Normalize identities (and possibly references) into separate tables for FK-based validation.
      - Choose between hashed vs natural-key representation depending on your priorities (index size vs readability).
  - “Join table over documents” approach (option 3):
      - Use pure DocumentId-based FKs in per-relationship tables instead of alias hashes.
      - Push natural-key resolution into application code or identity tables.
  - “JSON-aware constraint” approach (options 4, 5):
      - Use computed columns + FKs or trigger-based validation that work directly on JSON, leveraging engine-specific JSON support.
      - Trade generic schema for fewer side tables.

  If you’d like, I can pick one of these families (e.g., per-relationship join tables over Document or per-resource identity sidecars) and work through a concrete end-to-end design: DDL sketches,
  how inheritance like EducationOrganization/School is handled, and how inserts/updates/deletes would flow compared to the current DMS design.




## Refinement 1

Prompt:
› A big problem with the current design is the amount of churn that happens with the Reference table given that we are externalizing all references. I think we need a design that leans more on FKs
  to support validation. I don't like adding tons of computed columns or relying on trigger correctness. Thoughts?



• You’re bumping into a real limitation of the current design: as long as every logical reference becomes a row in dms.Reference, reference validation inherently means a lot of churn (delete +
  reinsert or upsert) on a very hot, very large table.

  If you don’t want to rely on triggers or computed columns, and you want to “lean on FKs”, then the only way to get the database to help you is to expose references as real relational columns
  somewhere, not only as JSON or hashed IDs in a generic reference table. The design question becomes: where do those columns live, and at what granularity?

  Below are a couple of concrete directions that move validation onto FKs over more stable tables and greatly reduce Reference churn.

  ———

  ### 1. “Relational Core” per Resource (Identity + References)

  Idea: keep the single Document table for JSON, but introduce a generated relational core: one table per resource type that has only:

  - The natural key fields (identity),
  - The references that are semantically important for that resource (often the same ones that appear in natural keys for association resources),
  - A 1:1 FK to Document.

  Example:

  - Document(DocumentPartitionKey, Id, ResourceName, ResourceVersion, EdfiDoc, …) — as today.

  For Student:

  - `StudentCore(
    DocumentPartitionKey,
    DocumentId PK/FK → Document,
    StudentUniqueId,
    /* maybe optional ref columns like SchoolId if you want */
    UNIQUE(StudentUniqueId)
    DocumentId PK/FK → Document,
    SectionIdentifier,
    SchoolId,
    SchoolYear,
    SessionName,
    /* natural key fields, many of which are references */
    UNIQUE(SectionIdentifier, SchoolId, SchoolYear, SessionName),
    FK (SchoolId) → SchoolCore(SchoolId),   -- validates the school reference
    FK (SchoolYear) → SchoolYearCore(SchoolYear),
    FK (SessionName, SchoolYear, SchoolId) → SessionCore(...)
    SchoolYear,
    SessionName,
    UNIQUE(StudentUniqueId, SectionIdentifier, SchoolId, SchoolYear, SessionName),
    FK (StudentUniqueId) → StudentCore(StudentUniqueId),
    FK (SectionIdentifier, SchoolId, SchoolYear, SessionName) → SectionCore(...)
      - Delete from *Core. FK failures indicate other resources still depend on it.

  Why this helps your churn problem:

  - You no longer have one row per reference in a global Reference table; you have at most one *Core row per document.
  - For association/event resources (which generate a huge amount of references), you’re already forced to have one document per relationship. Making those relationships explicit via FKs in *Core
    doesn’t increase row count; it just captures references where they already exist in the natural key.
  - Updates that don’t change identity/reference fields don’t touch *Core at all; you just patch JSON in Document.

  Tradeoffs:

  - You trade “three generic tables” for one core table per resource. However:
      - These tables can be generated from ApiSchema.json (similar to Query tables), so you’re not writing them by hand.
      - They only carry identity + reference fields, not all the other attributes, so they’re much thinner than full ODS tables.
  - You still have to decide what to do with “body references” that are not part of natural keys (e.g., arrays of references inside the payload). See next option for that.

  ———

  ### 2. Core FKs for Identity-Level References + Small Join Tables for Arrays

  You can push FK-based validation onto the relational core for identity-level references, and use a much smaller, targeted reference layer only for:

  - Arrays of references, or
  - Non-identity references you truly care about validating.

  Pattern:

  - Keep *Core tables from option 1 for identities and the references that appear in resource keys.
  - For a repeated reference field like studentContactAssociations[*].contactReference, you add a small join table:
      - `StudentContactRef(
        ParentDocumentPartitionKey,
        ParentDocumentId FK → Document,
        ContactIdentityKeyParts...,    -- columns that identify Contact in ContactCore
        FK → ContactCore(...)
          - FKs from those join tables to *Core validate existence.

  Why this is better than today:

  - You no longer externalize every reference for every document into one monolithic Reference table.
  - Most of the high‑cardinality relationships (SSA, Section, AttendanceEvent, etc.) are validated by FKs inside their own *Core tables, not by a global Reference table.
  - Only the “hard” references (deep arrays, optional fields) get externalized, and even then into many small, type‑specific tables that churn locally rather than globally.

  Tradeoffs:

  - More tables, but all schema is mechanically derived from the API model.
  - You have to choose which references are “worth” externalizing for validation vs leaving as best‑effort.

  ———

  ### 3. Keeping Aliases but Moving FKs off the Hot Reference Table

  If you want to preserve the “compute a hash for each identity” idea, but reduce Reference churn:

  - Keep Document + Alias (or an Identity table) as the authoritative “what exists” set.
  - Introduce *Core tables that reference Alias (instead of other *Core tables) for validation:
      - `StudentSectionAssociationCore(
        DocumentPartitionKey,
        DocumentId PK/FK → Document,
        StudentReferentialId UUID,
        SectionReferentialId UUID,
        FK (StudentReferentialId) → Alias(ReferentialId),
        FK (SectionReferentialId) → Alias(ReferentialId)

  - FK-based validation on stable per-document rows (the *Core tables).
  - The option to keep or discard a global Reference table depending on whether you need it for cascading and reverse lookups.

  ———

  ### Why avoiding triggers/computed columns pushes you this way

  Given your constraints:

  - Want: FK-based validation (DB guarantees reference correctness).
  - Don’t want: Many computed columns, JSON expression FKs, or trigger-heavy logic.
  - Reality: FKs only see normal columns; they can’t look “into” JSON or into a hash that the DB didn’t compute.

  So structurally you’re left with:

  - Some form of normalized projection (identity + refs) of each document into relational tables, where the FKs live.
  - Or a global Reference table (your current design), which is effectively “normalized to the individual reference instance” and therefore maximally churny.

  Moving the normalization boundary “up” from “per reference instance” to “per document identity” (and optionally a handful of join tables for arrays) is the main lever you have to reduce churn
  while still letting the database enforce referential integrity.

  ———

  If you’d like to explore one direction more concretely, I can take a specific high-volume resource pair (e.g., StudentSectionAssociation + StudentSectionAttendanceEvent) and sketch the exact *Core
  tables and DDL, showing how many rows you’d save vs the current Reference design and what the write path would look like step by step.
