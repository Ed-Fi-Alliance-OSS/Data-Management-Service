› Let's redesign the EducationOrganizationHierarchy table. It needs to handle scenarios where both a parent can have many children, but also that children can have many parents.


• Yes, we should keep an EducationOrganization hierarchy, but redesign it so it’s a proper many‑to‑many graph instead of the current “one parent per row” shape.

  Here’s a concrete redesign that supports:

  - Many children per parent.
  - Many parents per child.
  - Efficient ancestor lookups for auth expansion.

  ———

  1) Separate node and relationship tables

  Instead of overloading a single table with node + parent pointer, split into:

  - dms.EducationOrganization – one row per EdOrg “node”.
  - dms.EducationOrganizationRelationship – one row per parent/child edge.

  This gives a proper adjacency list that can represent DAGs and multi-parent relationships.

  dms.EducationOrganization

  Represents the EdOrg itself and links back to the source Document row so deletes cascade.

  CREATE TABLE IF NOT EXISTS dms.EducationOrganization (
      EducationOrganizationId BIGINT PRIMARY KEY,          -- global EdOrg key
      ProjectName             VARCHAR(256) NOT NULL,
      ResourceName            VARCHAR(256) NOT NULL,
      DocumentId              BIGINT       NOT NULL,
      DocumentPartitionKey    SMALLINT     NOT NULL,

      CONSTRAINT FK_EdOrg_Document
          FOREIGN KEY (DocumentId, DocumentPartitionKey)
          REFERENCES dms.Document (Id, DocumentPartitionKey)
          ON DELETE CASCADE
  );

  -- Optional uniqueness across project/resource/doc, if needed
  CREATE UNIQUE INDEX IF NOT EXISTS UX_EdOrg_Document
      ON dms.EducationOrganization (ProjectName, ResourceName, DocumentPartitionKey, DocumentId);

  - One row per EdOrgId (per database); if you need project-level isolation, you can widen the PK to (ProjectName, ResourceName, EducationOrganizationId) and keep a surrogate key.
  - Delete an EdOrg document → cascades to the EdOrg node and all its relationships.

  dms.EducationOrganizationRelationship

  Represents parent/child edges between EdOrgs.

  CREATE TABLE IF NOT EXISTS dms.EducationOrganizationRelationship (
      EducationOrganizationId        BIGINT NOT NULL,   -- child
      ParentEducationOrganizationId  BIGINT NOT NULL,   -- parent

      PRIMARY KEY (EducationOrganizationId, ParentEducationOrganizationId),

      CONSTRAINT FK_EdOrgRelationship_Child
          FOREIGN KEY (EducationOrganizationId)
          REFERENCES dms.EducationOrganization (EducationOrganizationId)
          ON DELETE CASCADE,

      CONSTRAINT FK_EdOrgRelationship_Parent
          FOREIGN KEY (ParentEducationOrganizationId)
          REFERENCES dms.EducationOrganization (EducationOrganizationId)
          ON DELETE CASCADE
  );

  -- Helpful indexes for traversals in both directions
  CREATE INDEX IF NOT EXISTS IX_EdOrgRelationship_Child
      ON dms.EducationOrganizationRelationship (EducationOrganizationId);

  CREATE INDEX IF NOT EXISTS IX_EdOrgRelationship_Parent
      ON dms.EducationOrganizationRelationship (ParentEducationOrganizationId);

  - A child can have many parents: multiple rows with same EducationOrganizationId, different ParentEducationOrganizationId.
  - A parent can have many children: multiple rows with same ParentEducationOrganizationId, different EducationOrganizationId.
  - PK ensures no duplicate edges for the same (child, parent) pair.

  ———

  2) Ancestor expansion for auth

  SubjectEdOrg needs “all EdOrgs this subject belongs to”, including ancestors. With the new schema, the ancestor function becomes:

  CREATE OR REPLACE FUNCTION dms.GetEducationOrganizationAncestors(
      p_educationOrganizationId BIGINT
  )
  RETURNS TABLE (EducationOrganizationId BIGINT)
  LANGUAGE plpgsql
  AS $$
  BEGIN
      RETURN QUERY
      WITH RECURSIVE ancestors AS (
          -- Start with the given organization
          SELECT eor.EducationOrganizationId, eor.ParentEducationOrganizationId
          FROM dms.EducationOrganizationRelationship eor
          WHERE eor.EducationOrganizationId = p_educationOrganizationId

          UNION ALL

          -- Walk up through all parents (multi-parent supported)
          SELECT parent.EducationOrganizationId, parent.ParentEducationOrganizationId
          FROM dms.EducationOrganizationRelationship parent
          JOIN ancestors a
            ON parent.EducationOrganizationId = a.ParentEducationOrganizationId
      )
      SELECT DISTINCT
          COALESCE(ParentEducationOrganizationId, EducationOrganizationId) AS EducationOrganizationId
      FROM ancestors;
  END;
  $$;

  Or more explicitly:

  - Base: start at p_educationOrganizationId.
  - Recursive step: follow EducationOrganizationRelationship edges “upwards” (from child to parent).
  - Collect distinct EducationOrganizationIds (including the starting node, if desired).

  This function is called when:

  - Populating SubjectEdOrg for a subject and a “base” EdOrg from a relationship document.
  - Optionally, expanding a caller’s EdOrgIds once per request.

  Because depths are small in Ed-Fi hierarchies, a recursive CTE over this adjacency list is fast with the child/parent indexes above.

  ———

  3) How this fits with the new auth model

  - EducationOrganization + EducationOrganizationRelationship are now purely hierarchy metadata:
      - Used at write time to precompute SubjectEdOrg rows for students, staff, contacts, etc.
      - Not consulted directly on every data read.
  - SubjectEdOrg holds the ancestor-expanded EdOrg memberships that DocumentSubject and DocumentIndex use at read time:
      - Reads do: DocumentIndex → DocumentSubject → SubjectEdOrg → caller’s authorized_edorg_ids.
  - Compared to the old design:
      - We keep only the minimal relational representation needed for hierarchy.
      - We drop:
          - Surrogate Id + ParentId pointing back into the same table.
          - EducationOrganizationHierarchyTermsLookup and its triggers.
          - Any JSONB/GIN-based hierarchy arrays.

  This redesign gives you a flexible, many-to-many EdOrg graph that’s easy to reason about, cheap to traverse for ancestor expansion, and cleanly supports the new SubjectEdOrg–based authorization model.
