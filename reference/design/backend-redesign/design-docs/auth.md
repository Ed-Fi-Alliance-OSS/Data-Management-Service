# Authorization Design for Relational Primary Store (Tables per Resource)

## Why not use `dms.DocumentSubject`
This design does not use the proposed `dms.DocumentSubject` table for the following reasons:
  - The `dms.DocumentSubject` table could grow very large if the EdOrg hierarchy is deep, potentially requiring partitioning.
  - Avoiding *phantoms* when the hierarchy is updated requires special consideration (such as a locking table). For example, if a `StudentSchoolAssociation.SchoolId` changes, we need to remove the old School authorization from the related Contacts and grant them the new School authorization. If a StudentContactAssociation is created concurrently during this process, the Contact might not receive the new School authorization.
  - The `dms.DocumentSubject` approach assumes that PrimaryAssociations (such as StudentSchoolAssociation and StudentContactAssociation) are seldom created, updated, or deleted; however, we have no usage statistics to confirm this.

ODS's authorization logic has been tested over the years, and its performance characteristics are known and accepted in the field. Therefore, the rest of this document follows the same approach as ODS, with minor optimizations such as reducing the number of auth-related DB roundtrips to zero by batching all auth queries with other roundtrips.

## Relationship-based authorization strategies
### How it works in ODS
This section provides a brief explanation of how the `RelationshipsWith*` authorization strategies work in ODS. In this example, we authorize CRUD operations for the `CourseTranscript` resource.

CourseTranscript has the following fields that are considered `securableElements` (already available in ApiSchema.json):
- EducationOrganizationId
- StudentUSI

The `RelationshipsWithEdOrgsAndPeople` strategy states that securableElements related to EdOrgs or People participate in the authorization decision. If CourseTranscript had a `Namespace` securableElement, it would be ignored by this strategy. Similarly, if we used the `RelationshipsWithEdOrgsOnly` strategy, the `StudentUSI` securableElement would be ignored.

In this example, we authorize using the RelationshipsWithEdOrgsAndPeople strategy, meaning both securableElements participate in the authorization decision. The token must have access to all securableElements (they are always combined with AND).

The strategy logic iterates through the securableElements and constructs a DB view/table name following the convention `auth.EducationOrganizationIdTo{securableElementName}`. For this example, the securableElements are authorized using the following DB views/tables:
- auth.EducationOrganizationIdToEducationOrganizationId
- auth.EducationOrganizationIdToStudentUSI

For single-record authorization, ODS executes the following query using these auth views/tables:
```sql
SELECT 
  CASE WHEN (
    EXISTS (
      SELECT 1 
      FROM 
        auth.EducationOrganizationIdToEducationOrganizationId AS authvw 
      WHERE 
        authvw.TargetEducationOrganizationId = @EducationOrganizationId 
        AND authvw.SourceEducationOrganizationId IN ( {EdOrgIdsFromToken} )
    ) 
    AND EXISTS (
      SELECT 1 
      FROM 
        auth.EducationOrganizationIdToStudentUSI AS authvw 
      WHERE 
        authvw.StudentUSI = @StudentUSI 
        AND authvw.SourceEducationOrganizationId IN ( {EdOrgIdsFromToken} )
    )
  ) THEN 1 ELSE 0 END AS IsAuthorized
```

To authorize multiple records (e.g., in the GetByQuery scenario), ODS executes the following query:
```sql
WITH authView299284 AS (
  SELECT 
    DISTINCT av.TargetEducationOrganizationId 
  FROM 
    auth.EducationOrganizationIdToEducationOrganizationId AS av 
  WHERE 
    av.SourceEducationOrganizationId IN ( {EdOrgIdsFromToken} )
), 
authView251e52 AS (
  SELECT 
    DISTINCT av.StudentUSI 
  FROM 
    auth.EducationOrganizationIdToStudentUSI AS av 
  WHERE 
    av.SourceEducationOrganizationId IN ( {EdOrgIdsFromToken} )
) 
SELECT 
  r.AggregateId, 
  r.AggregateData, 
  r.LastModifiedDate 
FROM 
  edfi.CourseTranscript AS r 
  INNER JOIN authView299284 ON r.EducationOrganizationId = authView299284.TargetEducationOrganizationId 
  INNER JOIN authView251e52 ON r.StudentUSI = authView251e52.StudentUSI 
ORDER BY 
  r.AggregateId OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

When multiple authorization strategies are configured for a given resource, relationship-based strategies are combined with OR, while the remaining strategies are combined with AND. For example: (`RelationshipsWithEdOrgsAndPeopleInverted` OR `RelationshipsWithEdOrgsAndPeople`) AND `NamespaceBased`.

#### Inverted strategies
Traditionally, when a token has access to a parent EducationOrganization, it implicitly has access to the child EducationOrganizations (e.g., a token with LEA access also has access to its Schools). However, some use cases require the reverse: access to a child EducationOrganization should grant access to its parent EducationOrganizations. An example is described in [ODS-2092](https://edfi.atlassian.net/browse/ODS-2092). This is where inverted relationships come into play, such as the `RelationshipsWithEdOrgsAndPeopleInverted` strategy.

Consider the `Course` resource, which uses the following authorization strategies for GET requests:
- RelationshipsWithEdOrgsAndPeople
- RelationshipsWithEdOrgsAndPeopleInverted

When a GET-by-ID request is made for Course, ODS authorizes it with:
```sql
SELECT 
  CASE WHEN (
    EXISTS (
      SELECT 1 
      FROM 
        auth.EducationOrganizationIdToEducationOrganizationId AS authvw 
      WHERE 
        authvw.TargetEducationOrganizationId = @EducationOrganizationId 
        AND authvw.SourceEducationOrganizationId IN ( {EdOrgIdsFromToken} ) -- Traditional top-to-bottom filter
    )
  ) 
  OR (
    EXISTS (
      SELECT 1 
      FROM 
        auth.EducationOrganizationIdToEducationOrganizationId AS authvw 
      WHERE 
        authvw.SourceEducationOrganizationId = @EducationOrganizationId 
        AND authvw.TargetEducationOrganizationId IN ( {EdOrgIdsFromToken} ) -- Inverted, bottom-to-top filter
    )
  ) THEN 1 ELSE 0 END AS IsAuthorized
```


### What needs to be done in DMS
The `auth.EducationOrganizationIdToEducationOrganizationId` table indicates which EdOrgIds are accessible from a given EdOrgId, either directly or indirectly. Because EducationOrganizations are rarely modified, ODS maintains this table using triggers ([here](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/6.0.0/Artifacts/MsSql/Structure/Ods/1302-CreateEdOrgToEdOrgTriggers.sql)). DMS should follow a similar approach: the DDL generation utility should create the `auth.EducationOrganizationIdToEducationOrganizationId` table and the related triggers.

In contrast, PrimaryAssociations are modified frequently. For example, determining whether a Contact is accessible by a given EdOrgId becomes expensive when StudentSchoolAssociations or StudentContactAssociations change. This is why ODS uses views rather than triggers to maintain this information (e.g., the `auth.EducationOrganizationIdToContactUSI` view). The DMS DDL generation utility should also create these views.

It is currently unclear whether the Student's DocumentId (and other People's DocumentIds) will appear in tables that indirectly reference the Student table (e.g., will the Student's DocumentId appear in `StudentAssessmentRegistration`?). If so, these auth views should return the authorized DocumentIds; otherwise, they should return the Person UniqueId. The same consideration applies to the `auth.EducationOrganizationIdToEducationOrganizationId` table.

To avoid triggers, we could maintain the `auth.EducationOrganizationIdToEducationOrganizationId` table in C#. The recommendation is to start with the same triggers as ODS for DMS v1.0 to save development time; we can migrate the logic to C# afterward.

At first glance, the triggers that maintain the `auth.EducationOrganizationIdToEducationOrganizationId` table do not appear to be phantom-safe. EducationOrganizations likely change so rarely that phantoms are unlikely to occur in practice. However, if we migrate the triggers to C#, we should account for phantoms and introduce a locking table if necessary, because performing this logic in C# adds latency due to DB roundtrips.


### Performance improvements over ODS
ODS executes an additional DB roundtrip for single-record authorizations, presumably because NHibernate limitations make batching difficult. In DMS, we have fine-grained control over the SQL queries we execute. On average, we can reduce DB roundtrips by roughly 50%, resulting in improved latency compared to ODS. This can also modestly improve throughput by reducing the number of transactions waiting on other transactions to complete.

Below are the expected DB roundtrips per operation.

#### PUT
- Roundtrip #1
  - Check that the resource exists by its ID (throw otherwise)
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Run authorization check using the values from the request body (throw if unauthorized)
  - Retrieve the referenced resources' DocumentIds (using `dms.ReferentialIdentity`)
  - Retrieve the resource-specific tables (for delta calculation)
- Roundtrip #2
  - Check that the provided etag matches (if applicable) (throw otherwise)
  - Execute update

ODS requires at least 4 roundtrips for the same operation, and more if the resource has child tables.

#### POST
- Roundtrip #1
  - Run authorization check using the values from the request body (throw if unauthorized)
  - Retrieve the referenced resources' DocumentIds (using `dms.ReferentialIdentity`)
- Roundtrip #2
    - If a record with the same identifying values already exists:
      - Inline PUT Roundtrip #1 steps (excluding the steps already executed in the first roundtrip)
    - Otherwise:
      - Insert into `dms.Document` and return its generated ID
- Roundtrip #3
  - Insert into the resource-specific tables, or inline PUT Roundtrip #2 steps (if applicable)

ODS requires at least 4 roundtrips for the same operation, and more if the resource has child tables.

#### DELETE
- Roundtrip #1
  - Check that the resource exists by its ID (throw otherwise)
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Check that the provided etag matches (if applicable) (throw otherwise)
  - Execute delete

ODS requires 3 roundtrips for the same operation.

#### GET (by Id)
- Roundtrip #1
  - Check that the resource exists by its ID (throw otherwise)
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Check that the provided etag matches (if applicable) (throw otherwise)
  - Get by Id

ODS requires 2 roundtrips for the same operation.

#### GET (by Query)
- Roundtrip #1
  - Get joining with auth table(s)

ODS also requires 1 roundtrip for the same operation.

---

NOTE: The order of some operations may vary depending on the resource, because certain resources must not disclose their existence to unauthorized clients.

NOTE: These counts do not include DB roundtrips related to authentication, which are typically served from the cache.

---

#### Batched SQL statements example
The following example demonstrates batching SQL statements in a single DB roundtrip. This example is PostgreSQL-specific, but SQL Server supports equivalent functionality with different syntax.

```csharp
var cmd = new NpgsqlCommand(@"
DO $$
BEGIN
    IF NOT EXISTS ( {auth check here} ) THEN
        RAISE EXCEPTION 'Unauthorized'
            USING ERRCODE = 'P0001';
    END IF;
END $$;

-- Rest of statements
SELECT A, ...
SELECT B, ...
", conn);

try
{
    using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
      // Read the first result set ...
    }

    // Move to the next result set
    await reader.NextResultAsync();

    while (await reader.ReadAsync())
    {
        // Read the second result set ...
    }
}
catch (PostgresException ex) when (ex.SqlState == "P0001")
{
   // Handle Unauthorized error ...
}
```

## View-based authorization strategy
The relationship-based authorization strategies described above are oriented around an API client's Education Organization Id claims and their relationships with other EdOrgs (through the EdOrg hierarchy) and with Students/Staff/Parents/Contacts through PrimaryAssociations.

However, some use cases require additional authorization restrictions based on student enrollment in specific courses (e.g., "Students enrolled in CTE courses") or grade levels (e.g., "Primary third grade students").

For example, suppose we want to return only CourseTranscripts whose student is enrolled in CTE courses. To do this, we add the `StudentWithCTECourseEnrollments` authorization strategy to the CourseTranscript resource and create a view with the same name:
```sql
CREATE OR REPLACE VIEW auth.StudentWithCTECourseEnrollments AS
SELECT DISTINCT
    ssa.StudentUSI
FROM
    edfi.StudentSectionAssociation ssa
        INNER JOIN edfi.CourseOffering co ON co.LocalCourseCode = ssa.LocalCourseCode
          AND co.SchoolId = ssa.SchoolId
          AND co.SchoolYear = ssa.SchoolYear
          AND co.SessionName = ssa.SessionName
        INNER JOIN edfi.CourseAcademicSubject csubj ON csubj.CourseCode = co.CourseCode
          AND csubj.EducationOrganizationId = co.EducationOrganizationId
        INNER JOIN edfi.descriptor d ON csubj.AcademicSubjectDescriptorId = d.descriptorid
WHERE
    d.CodeValue = 'Career and Technical Education';
```

The view must follow this naming convention: `{SecurableElementsResource}With{SomeDescription}`.

When a GET request for CourseTranscript arrives, we extract from the authorization strategy name the resource whose SecurableElements will be used for authorization. In this case, `auth.StudentWithCTECourseEnrollments` maps to `Student`, meaning we use the Student's securable elements to authorize CourseTranscript (all of Student's SecurableElements must also appear in CourseTranscript's SecurableElements).

These strategies are view-based (like relationship-based strategies) but are combined using AND semantics. As such, they serve as a means for applying additional filter criteria rather than defining new ways to associate Education Organizations and People. These strategies should be definable without requiring code changes, compilation, or deployment.

## Ownership-based authorization strategy
In ODS, this authorization strategy requires the `OwnershipBasedAuthorization` feature to be enabled (it's disabled by default). When enabled, it adds a `CreatedByOwnershipTokenId` column (smallint) to each root entity. The ApiClient must be configured with a `CreatorOwnershipTokenId`, which is used to set the `CreatedByOwnershipTokenId` column whenever a resource is created.

This authorization strategy is intended to be used in conjunction with existing relationship-based authorization strategies. More information is available on the [documentation page](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/ownership-based-authorization/).

Key considerations:
- If disabled, the `CreatedByOwnershipTokenId` column is NOT created on each root entity.
- If disabled, the Admin DB still has the related tables and columns, but with null values.
- If enabled and the user has not configured the ApiClient with a `CreatorOwnershipTokenId`, records are stamped with a null `CreatedByOwnershipTokenId`.
- If enabled and the `CreatorOwnershipTokenId` is configured on the ApiClient, all created records are stamped regardless of whether the resource is configured to use the `OwnershipBased` strategy.
- An ApiClient can read/modify multiple OwnershipTokens (defined in the `ApiClientOwnershipTokens` table), but it has only one `CreatorOwnershipTokenId` used to stamp created records.
- There is no unique constraint on `ApiClients.CreatorOwnershipTokenId` in the Admin DB, meaning multiple ApiClients can share the same CreatorOwnershipTokenId.
- Users can transfer an ApiClient's `CreatorOwnershipTokenId` to a different ApiClient.

When a GET-by-query request is made for Students, ODS authorizes it with:
```sql
SELECT 
  r.AggregateId, 
  r.AggregateData, 
  r.LastModifiedDate, 
  r.StudentUsi AS SurrogateId 
FROM 
  edfi.Student AS r 
WHERE 
  r.StudentUSI = @p0 
  AND r.StudentUniqueId = @p1 
  AND CreatedByOwnershipTokenId IN ( {ApiClientOwnershipTokens} ) 
ORDER BY 
  r.AggregateId OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

GET-by-ID, Update, and Delete are authorized by first retrieving the resource from the DB and materializing it in C#, then checking whether the ApiClient has an OwnershipToken that matches the resource's. This may consume resources unnecessarily if the client is not authorized. In DMS, we could optimize it so that it doesn't retrieve the resource from the database in this situation.

ODS does not have a shared table where all resource entries are tracked (analogous to `dms.Document`), so it needs to create the `CreatedByOwnershipTokenId` column on each root entity (only when the feature is enabled). In DMS, we can add the `CreatedByOwnershipTokenId` column to the `dms.Document` table as nullable (same as ODS) and always create it regardless of whether the `OwnershipBasedAuthorization` feature is enabled. We could also remove the feature flag entirely, since toggling its value does not require DB changes.

## Namespace-based authorization strategy
A Vendor can be configured with multiple namespace prefixes (such as `uri://ed-fi.org`). When a request arrives, ODS performs a prefix match to verify that the namespace assigned to the root entity begins with one of the namespace prefixes assigned to the API client.

For example, a GET-by-query request for GradebookEntry produces:
```sql
SELECT 
  r.AggregateId, 
  r.AggregateData, 
  r.LastModifiedDate 
FROM 
  edfi.GradebookEntry AS r 
WHERE 
  r.Namespace IS NOT NULL 
  AND (
    r.Namespace LIKE @p0 
    OR r.Namespace LIKE @p1 
    OR r.Namespace LIKE @p2
  ) 
ORDER BY 
  r.AggregateId OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
```

GET-by-ID, Update, Create, and Delete are authorized by retrieving the resource from the DB and materializing it in C#, then checking whether the ApiClient has a namespace prefix that matches the resource's. This may consume resources unnecessarily if the client is not authorized. In DMS, we could optimize it so that it doesn't retrieve the resource from the database in this situation.

Unlike the view-based strategies, for some operations of the Ownership-based and Namespace-based strategies (such as POST and PUT), the authorization check is performed in C# code before querying the DB.

## Row-level security
Both SQL Server and PostgreSQL support row-level security; however, the recommendation is to not use it for DMS v1.0 given the short development timeline and the uncertainty surrounding the feature. If we adopt it and it turns out to have show-stopping limitations or unacceptable performance, it could jeopardize the release.

## Open Questions
- Do we need the `IncludingDeletes` auth views? How will Change Queries be implemented?
- When should the view-based and ownership-based authorization strategies be implemented? Post DMS v1.0?
- Should the auth tables and views output DocumentIds or identifying columns? This depends on whether referenced DocumentIds appear in tables that *indirectly* reference another table.

## Tickets
The following is a work-in-progress list of tickets needed to implement the authorization strategies described above:
- Provide a way to get the DB column name for a given `SecurableElement`.
- The DDL generation utility should create the `auth.EducationOrganizationIdToEducationOrganizationId` table and the related triggers.
- The DDL generation utility should create the auth views.
- Remove `ProvideEducationOrganizationHierarchyMiddleware.cs`, as it is used to maintain the old EdOrgToEdOrg table.
- ...
