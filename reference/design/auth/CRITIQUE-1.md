• Overall assessment

  - The design is a substantial improvement over the current trigger/JSONB model: it’s much more relational, aligns with ODS semantics, and integrates cleanly with DocumentIndex. It is also reasonably RLS‑friendly for a future phase.
  - The main risks are (a) write‑path cost and concurrency of membership recomputation, (b) faithfully preserving complex ODS strategy semantics in the new SQL filter builder, and (c) getting the enum/metadata coordination for SubjectType/
    Pathway correct over time.

  ———

  Strengths

  - Data model clarity
      - DocumentSubject + SubjectEdOrg is a clean, generic replacement for multiple …Authorization/…SecurableDocument tables and JSONB arrays; it matches ODS’s conceptual “subject + EdOrg membership” model well.
      - Separating “what the document is about” (DocumentSubject) from “where the subject is authorized” (SubjectEdOrg) makes it easy to support new pathways later without schema explosion.
      - Reusing the existing EducationOrganizationHierarchy adjacency plus GetEducationOrganizationAncestors is simpler than maintaining TermsLookup tables and complex triggers.
  - Read‑path performance design
      - The DocumentIndex CTE pattern is solid: GIN on QueryFields + hash partitioning by (ProjectName, ResourceName) plus narrow joins for authorization is exactly what you want for high‑volume /data queries.
      - The proposed authorized paging query (EXISTS joining DocumentSubject and SubjectEdOrg) can be made very efficient with the right composite indexes, and keeps pagination over already‑authorized rows.
      - Using QueryFields as the single source for both user query predicates and namespace‑based auth simplifies index strategy and avoids a second set of JSONB indexes.
  - Write‑path simplification
      - Eliminating authorization triggers on dms.Document and moving logic into C# (SubjectMembershipWriter) is a big win for debuggability, deployment, and cross‑DB portability.
      - Subject‑centric recomputation per (SubjectType,SubjectIdentifier,Pathway) is simple to reason about, avoids incremental “drift” bugs, and mirrors ODS’s “derive from relationships” philosophy.
  - Semantics and future RLS
      - The model preserves the ODS concepts of relationship‑based strategies, namespace‑based strategies, and strategy composition; it’s compatible with existing claimset metadata and IAuthorizationRepository.
      - From an RLS perspective, this schema is usable later: you can imagine policies on dms.Document that EXISTS a row in DocumentSubject/SubjectEdOrg matching session EdOrg state, while leaving /data queries structurally similar
        (DocumentIndex joins Document).

  ———

  Concerns / potential issues

  - Write‑path cost and concurrency
      - Recompute‑on‑every‑change is robust but may be expensive for high‑churn subjects (e.g., students with many StudentSchoolAssociation rows, or bulk loads); worst‑case complexity is O(n²) per subject during initial load or heavy churn.
      - The design is light on details about concurrency: if two requests mutate relationships for the same subject concurrently, you can get “last writer wins” races in SubjectEdOrg unless the membership writer uses consistent locking
        (e.g., per‑subject SELECT … FOR UPDATE or advisory locks) and runs inside the same transaction as the relationship write.
      - It’s not yet specified whether recompute queries read from Document or DocumentIndex; using DocumentIndex filtered by QueryFields would be preferable to avoid JSONB scans on EdfiDoc.
  - Indexing details on the new tables
      - SubjectEdOrg
          - The proposed indexes ((SubjectType,SubjectIdentifier,Pathway) and (EducationOrganizationId)) are a good start, but the read‑path EXISTS is most efficient if you can drive from a small set of
            (SubjectType,SubjectIdentifier,Pathway) values for each document and then filter by EducationOrganizationId.
          - A composite index like (SubjectType,SubjectIdentifier,Pathway,EducationOrganizationId) would better support that nested‑loop pattern and avoid extra filtering on EducationOrganizationId.
      - DocumentSubject
          - Primary key includes (ProjectName,ResourceName,DocumentPartitionKey,DocumentId,SubjectType,SubjectIdentifier). For the main read query, you join on exactly those four leading columns, so the PK can serve as a usable index; that’s
            good.
          - However, the design doesn’t mention a FK to dms.Document (only the implicit key fields). Adding an explicit FK (DocumentPartitionKey,DocumentId) → dms.Document is worth considering for integrity and for planner statistics.
          - Storing ProjectName and ResourceName on every DocumentSubject row is redundant with dms.Document/DocumentIndex; if the goal is to limit cross‑project joins, this is fine, but it does increase key width and index size. If that’s
            not needed, you could simplify to (DocumentPartitionKey,DocumentId,SubjectType,SubjectIdentifier) and rely on joins to DocumentIndex/Document to get project/resource.
  - Strategy semantics vs SQL shape
      - The section on strategy composition (8.1–8.2) is conceptually sound but under‑specified in how it maps to SQL. Many ODS strategies are not just “subject with membership in (union of authorized EdOrgs)”, but also care about which kind
        of subject/pathway is present on a document and how multiple pathways combine (e.g., “students via school OR responsibility”, “EdOrgs AND Students”, “EdOrgs OR Staff or Contacts”).
      - The single EXISTS form with a flat EducationOrganizationId = ANY($4) and an optional “subject/type pathway predicates” comment is likely too simple for some of the more complex AND/OR strategy matrices. You may need:
          - Multiple EXISTS clauses (one per strategy or per pathway group), combined with AND/OR at the SQL level, or
          - A more structured expression builder that can generate nested boolean logic over subject/pathway filters.
      - Without a clear mapping from AuthorizationStrategyEvaluator trees to SQL fragments, it will be hard to prove that the new read‑path semantics match the existing ODS behavior exactly.
  - Enum / metadata robustness
      - SubjectType and Pathway are stored as smallint, and the design defers their governance to “future refinement”. This is fragile: any mismatch between C# enums and DB values will lead to silent mis‑authorization.
      - I strongly recommend introducing small lookup tables for SubjectType and Pathway (with stable string codes), and having C# bind by name rather than by numeric value, or at least generating the numeric mapping from a single shared
        source.
  - Hierarchy change handling
      - Deferring SubjectEdOrg reconciliation for EdOrg hierarchy changes to a background job is reasonable given low frequency, but the design doesn’t outline:
          - How you detect the need for a full rebuild vs a targeted one.
          - How you avoid windows where newly changed hierarchy data and stale SubjectEdOrg rows coexist and give surprising authorization results.
      - For correctness, I’d at least standardize on one approach (e.g., “always recompute all memberships nightly” or “always recompute for affected EdOrgs and their subjects synchronously”) and document the maximum expected lag.
  - Reliance on QueryFields for namespace‑auth
      - Namespace‑based authorization is pushed entirely into QueryFields. That’s clean, but it assumes that every namespace‑secured resource has namespace defined as a QueryField in ResourceSchema.QueryFields. If a resource is misconfigured
        or a new resource is added without that query field, you won’t enforce namespace authorization on /data queries.
      - It would be good to explicitly state (and test) that MetaEd generation will always align SecurityElements/namespace with QueryFields, and fail fast if a namespace‑secured resource is missing the required projection.

  ———

  Suggestions / refinements

  - Tighten the indexing story:
      - Add a composite index on SubjectEdOrg(SubjectType,SubjectIdentifier,Pathway,EducationOrganizationId).
      - Consider an explicit FK from DocumentSubject to Document, and decide whether ProjectName/ResourceName redundancy is intentional or can be dropped.
  - Define a concrete SQL “expression builder” for strategies:
      - Document how each common ODS strategy (e.g., RelationshipsWithStudentsOnly, RelationshipsWithEdOrgsAndPeople) maps to one or more EXISTS clauses over DocumentSubject/SubjectEdOrg, and how AND/OR composition is reflected in SQL.
      - Use a small matrix of “tricky” ODS strategies as golden tests to avoid semantic drift.
  - Clarify membership recomputation mechanics:
      - Specify whether recompute queries use DocumentIndex or Document and which indexes they rely on.
      - Define the locking/isolation strategy (e.g., per subject advisory lock) to avoid concurrent recompute races.
  - Lock down enum/metadata mapping:
      - Introduce descriptor tables or a single shared enum source for SubjectType/Pathway, and bake validation into startup (fail if mappings don’t match expected rows).
  - For a future RLS phase:
      - Plan to put RLS on dms.Document (and possibly DocumentSubject/SubjectEdOrg) and ensure /data queries always join back to Document so RLS is enforced; avoid patterns that rely on DocumentIndex alone for counting/exists.
