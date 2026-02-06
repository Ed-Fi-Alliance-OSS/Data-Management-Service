# Authorization Design for Relational Primary Store (Tables per Resource)

## Why not use `dms.DocumentSubject`
This design doesn't use the proposed `dms.DocumentSubject` table because:
  - The `dms.DocumentSubject` table could become large if the EdOrg hierarchy is deep, potentially requiring partitioning.
  - Avoiding *phantoms* when the hierarchy gets updated requires special consideration (such as a locking table). For example, if a `StudentSchoolAssociation.SchoolId` changes we need to remove the old School authorization from the related Contacts, and grant them the new School authorization. If a StudentContactAssociation gets created in the middle of this process (concurrently), the Contact might not receive the new School authorization.
  - The `dms.DocumentSubject` approach assumes that PrimaryAssociations (like StudentSchoolAssociation, StudentContactAssociation, ...) are seldom created/updated/deleted; however, we don't have usage statistics that confirm this.

ODS's authorization logic has been tested over the years, and its performance characteristics are known and accepted in the field. Hence, the rest of this document follows the same approach as ODS, with minor optimizations such as reducing the number of auth-related DB roundtrips to 0 (by batching all the auth queries with other roundtrips).

## Relationship-based authorization strategies
### How it works in ODS
Let's start with a short explanation of how the `RelationshipsWith*` authorization strategies work in ODS. In this example, we will authorize CRUD operations for the `CourseTranscript` resource.

Consider that CourseTranscript has the following fields that are considered `securableElements` (already available in the ApiSchema.json):
- EducationOrganizationId
- StudentUSI

The `RelationshipsWithEdOrgsAndPeople` strategy states that securableElements that are related to EdOrgs or People participate in the authorization decision. If CourseTranscript had a `Namespace` securableElement, it would be ignored by this strategy. Similarly, if we were to use the `RelationshipsWithEdOrgsOnly` strategy then the `StudentUSI` securableElement would be ignored.

In this example, we will authorize using the RelationshipsWithEdOrgsAndPeople strategy, meaning that both securableElements will participate in the authorization decision. The token must have access to all of these securableElements (they are always combined with AND).

The strategy logic iterates through the securableElements and constructs a DB view/table name following the convention `auth.EducationOrganizationIdTo{securableElementName}`, meaning that the securableElements will be authorized using the following DB views/tables:
- auth.EducationOrganizationIdToEducationOrganizationId
- auth.EducationOrganizationIdToStudentUSI

Then, for single-record authorization, ODS executes the following query using the resulting auth views/tables:
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

To authorize multiple records, such as in the GetByQuery scenario, ODS executes the following query:
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

If multiple authorization strategies are configured for a given resource, relationship-based strategies are combined with OR and the remaining strategies are combined with AND. For example: (`RelationshipsWithEdOrgsAndPeopleInverted` OR `RelationshipsWithEdOrgsAndPeople`) AND `NamespaceBased`.

#### Inverted strategies
Traditionally, when a token has access to a parent EducationOrganization, it indirectly has access to the child EducationOrganizations (a token with LEA access also has access to its Schools). However, there are some use cases where having access to a child EducationOrganization should grant access to its parent EducationOrganizations. An example use case is described in [ODS-2092](https://edfi.atlassian.net/browse/ODS-2092). This is where inverted relationships come into play, such as the `RelationshipsWithEdOrgsAndPeopleInverted` strategy.

Consider the `Course` resource. It uses the following authorization strategies for GET requests:
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
The `auth.EducationOrganizationIdToEducationOrganizationId` table indicates which EdOrgIds are accessible from a given EdOrgId (either directly or indirectly). Given that EducationOrganizations are rarely modified, ODS maintains this table using triggers [here](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/6.0.0/Artifacts/MsSql/Structure/Ods/1302-CreateEdOrgToEdOrgTriggers.sql). DMS should follow a similar approach: the DDL generation utility should create the `auth.EducationOrganizationIdToEducationOrganizationId` table and the related triggers.

On the other hand, PrimaryAssociations are modified frequently. For example, determining whether a Contact is accessible by a given EdOrgId becomes expensive when StudentSchoolAssociations or StudentContactAssociations change. This is why ODS uses views rather than triggers to maintain this information (in the `auth.EducationOrganizationIdToContactUSI` view, for example). DMS's DDL generation utility should also create these views.

As of today, it's unclear whether the Student's DocumentId (and other People's DocumentIds) will appear in tables that indirectly reference the Student table (e.g., will the Student's DocumentId appear in `StudentAssessmentRegistration`?). If so, these auth views should return the authorized DocumentIds; otherwise, they should return the Person UniqueId. The same applies to the `auth.EducationOrganizationIdToEducationOrganizationId` table.

To avoid triggers, we could maintain the `auth.EducationOrganizationIdToEducationOrganizationId` table in C#. My recommendation is to start with the same triggers as ODS for DMS v1.0 to save development time; we can move the logic to C# afterwards.

At a quick glance, it seems that the triggers that maintain the `auth.EducationOrganizationIdToEducationOrganizationId` table are not phantom-safe. It's likely that EducationOrganizations change so rarely that phantoms don't have a chance to appear. If we decide to migrate the triggers to C#, we should consider phantoms and introduce a locking table if necessary, because calculating this in C# adds latency due to DB roundtrips.


### Performance improvements over ODS
ODS executes an additional DB roundtrip for single-record authorizations, presumably due to NHibernate limitations that make batching difficult. In DMS, we have fine-grained control over the SQL queries that we execute. On average, we could reduce at least ~50% of the DB roundtrips, resulting in improved latency compared to ODS. This can also improve throughput by a small amount due to fewer transactions waiting for other transactions to complete.

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

ODS executes at least 4 roundtrips to achieve the same, more if the resource has child tables.

#### POST
- Roundtrip #1
  - Run authorization check using the values from the request body (throw if unauthorized)
  - Retrieve the referenced resources' DocumentIds (using `dms.ReferentialIdentity`)
- Roundtrip #2
    - If a record with the same identifying values already exists
      - Inline PUT Roundtrip #1 steps (excluding the steps already executed in the first roundtrip)
    - Else
      - Insert into `dms.Document` and return its generated ID
- Roundtrip #3
  - Insert into the resource-specific tables. Or inline PUT Roundtrip #2 steps (if applicable)

ODS executes at least 4 roundtrips to achieve the same, more if the resource has child tables.

#### DELETE
- Roundtrip #1
  - Check that the resource exists by its ID (throw otherwise)
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Check that the provided etag matches (if applicable) (throw otherwise)
  - Execute delete

ODS executes 3 roundtrips to achieve the same.

#### GET (by Id)
- Roundtrip #1
  - Check that the resource exists by its ID (throw otherwise)
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Check that the provided etag matches (if applicable) (throw otherwise)
  - Get by Id

ODS executes 2 roundtrips to achieve the same.

#### GET (by Query)
- Roundtrip #1
  - Get joining with auth table(s)

ODS executes 1 roundtrip to achieve the same.

---

NOTE that the order of some operations might change depending on the resource, because for some resources we must not disclose their existence if unauthorized.

NOTE that this doesn't consider DB roundtrips related to authentication, which are usually served from the cache.

---

#### Batched SQL statements example
A simple example that showcases batching SQL statements in a single DB roundtrip is shown below. This example is PostgreSQL-specific, but SQL Server supports the same functionality with a different syntax.

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
The previous view-based authorization strategies are oriented around an API client's Education Organization Id claims and their relationship with other EdOrgs (through the EdOrg hierarchy of the model) and with Students/Staff/Parents/Contacts through PrimaryAssociations.

However, some use cases expand these assumptions to include authorization restrictions on student access based on enrollment in particular courses (e.g. "Students enrolled in CTE courses"), or based on grade levels ("Primary third grade students").

Let's assume we want to return only CourseTranscripts whose student is enrolled in CTE courses. To do this, we would add the `StudentWithCTECourseEnrollments` authorization strategy to the CourseTranscript resource and create a view with the same name:
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

When a GET request for CourseTranscript arrives, we extract from the authorization strategy name the resource whose SecurableElements will be used to authorize the request. In this case, `auth.StudentWithCTECourseEnrollments` maps to `Student`, meaning we will use the Student's securable elements to authorize CourseTranscript (all of Student's SecurableElements must appear in CourseTranscript's SecurableElements).

These strategies are view-based (like relationship-based strategies) but are combined using AND semantics. As such, they should be viewed as a means for applying additional filter criteria rather than for defining new alternative ways to associate Education Organizations and People. These strategies should be easily defined without requiring code changes, compilation, or deployment.

## Namespace-based authorization strategy
WIP

## Ownership-based authorization strategy
WIP

## Row-level security
Both SQL Server and PostgreSQL support row-level security; however, I recommend not using it for DMS v1.0 given the short development timeline and the uncertainty around the feature. If we adopt it and it turns out to have show-stopper limitations or is too slow, it could jeopardize the release.

## Open Questions
- Do we need the `IncludingDeletes` auth views? How will ChangeQueries be implemented?
- When should the View-based and Ownership-based authorization strategies be implemented? Post DMS v1.0?
- Should the auth tables and views output the DocumentIds or the identifying columns? Depends on whether referenced DocumentIds appear in tables that *indirectly* reference another table

## Tickets
A list of tickets needed to implement the authorization strategies listed above. This list is a work in progress:
- Need a way to get the DB column name of a given `SecurableElement`.
- The DDL generation utility should create the `auth.EducationOrganizationIdToEducationOrganizationId` table and the related triggers.
- The DDL generation utility should create the auth views.
- Delete the `ProvideEducationOrganizationHierarchyMiddleware.cs` since it's used to maintain the old EdOrgToEdOrg table.
- ...
