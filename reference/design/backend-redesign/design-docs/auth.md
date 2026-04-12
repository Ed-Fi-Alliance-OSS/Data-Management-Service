# Authorization Design for Relational Primary Store (Tables per Resource)

## Why not use `dms.DocumentSubject`

An earlier authorization redesign draft proposed a `dms.DocumentSubject` table that would map each document to its subject documents (Student, Staff, Contact, EdOrg). Combined with a materialized `dms.SubjectEdOrg` membership table, this approach aimed to precompute EdOrg membership during document insert/update (refer to the draft [here](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/8a69f58d6b05a1daa7754ce081070f27e283fd91/reference/design/backend-redesign/design-docs/auth.md)). This updated redesign does not use it for the following reasons:

- The `dms.DocumentSubject` table could grow very large if the EdOrg hierarchy is deep, potentially requiring partitioning.
- Avoiding *phantoms* when the hierarchy is updated requires special consideration (such as a locking table). For example, if a `StudentSchoolAssociation.SchoolId` changes, we need to remove the old School authorization from the related Contacts and grant them the new School authorization. If a StudentContactAssociation is created concurrently during this process, the Contact might not receive the new School authorization.
- The `dms.DocumentSubject` approach assumes that PrimaryAssociations (such as StudentSchoolAssociation and StudentContactAssociation) are seldom created, updated, or deleted; however, we have no usage statistics to confirm this.

ODS's authorization logic has been tested over the years, and its performance characteristics are known and accepted in the field. Therefore, the rest of this document follows the same approach as ODS, with minor optimizations such as reducing the number of auth-related DB roundtrips by batching all auth queries with other roundtrips.

## How authorization currently works in ODS

This section provides a brief explanation of how the authorization strategies work in ODS.

The `auth.EducationOrganizationIdToEducationOrganizationId` table indicates which EdOrgIds are accessible from a given EdOrgId, either directly or transitively. Because EducationOrganizations are rarely modified, ODS maintains this table using triggers ([here](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/6.0.0/Artifacts/MsSql/Structure/Ods/1302-CreateEdOrgToEdOrgTriggers.sql)).

PrimaryAssociations are modified frequently. Determining whether a Contact is accessible by a given EdOrgId becomes expensive when StudentSchoolAssociations or StudentContactAssociations change. This is why ODS uses views rather than triggers to maintain this information (e.g., the `auth.EducationOrganizationIdToContactUSI` view).

### What are SecurableElements

SecurableElements are the resource's fields that can participate in an authorization decision, such as the EducationOrganization and Student/Contact/Staff IDs.

Note that DMS already makes these fields available in the ApiSchema.json. Consider `CourseTranscript`:

```json
"securableElements": {
    "Contact": [
    ],
    "EducationOrganization": [
        {
            "jsonPath": "$.studentAcademicRecordReference.educationOrganizationId",
            "metaEdName": "EducationOrganizationId"
        }
    ],
    "Namespace": [
    ],
    "Staff": [
    ],
    "Student": [
        "$.studentAcademicRecordReference.studentUniqueId"
    ]
}
```

### Relationship-based authorization strategies

In this example, we will authorize CRUD operations for the `CourseTranscript` resource, which has the following fields that are considered `securableElements`:

- EducationOrganizationId
- StudentUSI

The `RelationshipsWithEdOrgsAndPeople` strategy states that securableElements related to EdOrgs or People participate in the authorization decision. If CourseTranscript had a `Namespace` securableElement, it would be ignored by this strategy. Similarly, if we used the `RelationshipsWithEdOrgsOnly` strategy, the `StudentUSI` securableElement would be ignored.

In this example, we authorize using the RelationshipsWithEdOrgsAndPeople strategy, meaning both securableElements participate in the authorization decision. The token must have access to all securableElements (they are always combined with AND).

The strategy logic iterates through the securableElements and constructs a DB view/table name following the convention `auth.EducationOrganizationIdTo{securableElementName}`. For this example, the securableElements are authorized using the following DB views/tables:

- auth.EducationOrganizationIdToEducationOrganizationId
- auth.EducationOrganizationIdToStudentUSI

For single-record authorization, ODS executes the following query using the above auth views/tables:

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

To authorize multiple entries, ODS executes the following query:

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

Traditionally, when a token has access to a parent EducationOrganization, it implicitly has access to the child EducationOrganizations (e.g., a token with LEA access also has access to its Schools). However, some use cases require the reverse: access to a child EducationOrganization should grant access to its parent EducationOrganizations. An example use case is described in [ODS-2092](https://edfi.atlassian.net/browse/ODS-2092). This is where inverted relationships come into play, such as the `RelationshipsWithEdOrgsAndPeopleInverted` strategy.

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
  OR ( -- Relationship-based strategies are combined with `OR`
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

#### List of Relationship-based strategies

The complete list of Relationship-based strategies is:

- RelationshipsWithEdOrgsAndPeople
- RelationshipsWithEdOrgsAndPeopleInverted
- RelationshipsWithEdOrgsOnly
- RelationshipsWithEdOrgsOnlyInverted
- RelationshipsWithPeopleOnly
- RelationshipsWithStudentsOnly
- RelationshipsWithStudentsOnlyThroughResponsibility

Excluding the `*Inverted` and `*ThroughResponsibility` strategies, these strategies are very similar; the only difference is which securableElements are included during authorization. For example, the `RelationshipsWithStudentsOnly` strategy ignores any EducationOrganization, Contact, and Staff that might appear in the resource.

The `RelationshipsWithStudentsOnlyThroughResponsibility` strategy uses the `EducationOrganizationIdToStudentUSIThroughResponsibility` view, which uses `StudentEducationOrganizationResponsibilityAssociation` instead of `StudentSchoolAssociation` to establish the relationship.

### View-based authorization strategy

Some use cases require additional authorization restrictions based on student enrollment in specific courses (e.g., "Students enrolled in CTE courses") or grade levels (e.g., "Primary third grade students").

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

The view must follow this naming convention: `{BasisResource}With{SomeDescription}`.

When a GET request for CourseTranscript arrives, if the configured authorization strategy name is unknown, we fall back to the custom view-based strategy and extract from the strategy name the *basis resource*. In this case, `auth.StudentWithCTECourseEnrollments` maps to `Student`. Then, we validate that all the primary key columns from `Student` appear in `CourseTranscript`. These columns will be used to join with the custom view and authorize the request.

Non-primary-key and role-named columns are allowed for the target resource ([more info here](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/511cf65e71b1f3d96a7e3801a3ed71dc84239e20/Application/EdFi.Ods.Common/Security/Authorization/CustomViewBasedAuthorizationStrategy.cs#L69)). For example, assume that `StudentUniqueId` is nullable in `CourseTranscript`; the strategy will allow it. However, for GET-many requests it will only return non-null values that match the result from the view, and for GET-by-ID it will return an unauthorized error if the entry has a null `StudentUniqueId`. Change query endpoints cannot be authorized with this strategy if it maps to non-PK columns in the target resource ([more info here](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/511cf65e71b1f3d96a7e3801a3ed71dc84239e20/Application/EdFi.Ods.Api/Security/AuthorizationStrategies/CustomViewBased/CustomViewBasedAuthorizationFilterDefinitionsFactory.cs#L147)).

When searching for the *basis resource*, we prioritize resources from the standard over resources from extensions (for example, `edfi.Student` gets selected instead of `homograph.Student`).

These strategies are view-based (like the relationship-based strategies) but are combined using AND semantics. As such, they serve as a means for applying **additional** filter criteria rather than defining new ways to associate Education Organizations and People.

These strategies can be defined without requiring code changes, compilation, or deployment; ODS refreshes the Claim Set metadata cache on a configured TTL to detect when a new custom view-based strategy is configured.

When the view that backs the custom strategy doesn't exist or returns invalid columns, ODS logs an error with the details and returns HTTP 500:
```json
{
  "detail": "An unexpected problem has occurred.",
  "type": "urn:ed-fi:api:system",
  "title": "System Error",
  "status": 500,
  "correlationId": "07690240-0391-49aa-9388-168da6e62df3"
}
```

As of now, the custom views are not validated during startup. It could be implemented, but such validation would also need to be executed whenever the Claim Set cache is refreshed.

### Ownership-based authorization strategy

In ODS, this authorization strategy requires the `OwnershipBasedAuthorization` feature to be enabled (it's disabled by default). When enabled, it adds a `CreatedByOwnershipTokenId` column (smallint) to each root entity. The ApiClient must be configured with a `CreatorOwnershipTokenId`, which is used to set the `CreatedByOwnershipTokenId` column whenever a resource is created.

This authorization strategy is intended to be used in conjunction with existing relationship-based authorization strategies. More information is available on our [documentation page](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/ownership-based-authorization/).

Key considerations:

- If disabled, the `CreatedByOwnershipTokenId` column is NOT created on each root entity.
- If disabled, the Admin DB still has the related tables and columns, but with null values.
- If enabled and the user has not configured the ApiClient with a `CreatorOwnershipTokenId`, entries are stamped with a null `CreatedByOwnershipTokenId`.
- If enabled and the `CreatorOwnershipTokenId` is configured on the ApiClient, all created entries are stamped regardless of whether the resource is configured to use the `OwnershipBased` strategy.
- An ApiClient can read/modify multiple OwnershipTokens (defined in the `ApiClientOwnershipTokens` table), but it has only one `CreatorOwnershipTokenId` used to stamp created entries.
- There is no unique constraint on `ApiClients.CreatorOwnershipTokenId` in the Admin DB, meaning multiple ApiClients can share the same CreatorOwnershipTokenId.
- Users can transfer an ApiClient's `CreatorOwnershipTokenId` to a different ApiClient.
- Entries with `CreatedByOwnershipTokenId=null` are not returned.

When a GET-many request is made for Students, ODS authorizes it with:

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

GET-by-ID, Update, and Delete are authorized by first retrieving the resource from the DB and materializing it in C#, then checking whether the ApiClient has an OwnershipToken that matches the resource's. This consumes resources unnecessarily if the client is not authorized.

### Namespace-based authorization strategy

A Vendor can be configured with multiple namespace prefixes (such as `uri://ed-fi.org`). When a request arrives, ODS performs a prefix match to verify that the namespace assigned to the root entity begins with one of the namespace prefixes assigned to the API client.

For example, a GET-many request for GradebookEntry produces:

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

Similar to the Ownership-based strategy, GET-by-ID, Update, Create, and Delete are authorized by retrieving the resource from the DB and materializing it in C#, then checking whether the ApiClient has a namespace prefix that matches the resource's. This consumes resources unnecessarily if the client is not authorized.

### Execution order

When multiple authorization strategies are configured for a resource, the order in which they execute is important because, when the request is not authorized, ODS returns the error message of the first strategy that failed.

Strategies that are combined with `AND` are executed first:

- Namespace-based
- Custom view-based
- Ownership-based

Within this list, ODS states that Ownership-based must be executed last. Namespace-based and Custom view-based are executed in the order they were set up in the Admin DB.

Strategies that are combined with `OR` (Relationship-based) execute afterward. The order in which each `OR` strategy gets executed doesn't matter because we combine (concatenate) and return the error hints of all of them, regardless of whether only one failed.

When updating a resource, we first authorize against the values that are currently stored in the DB, and then authorize against the new values (the ones that come in the request body, also known as the proposed values).

### Securable elements must be initialized

As of today, all fields that are securable elements must be part of the resource's identity (except for the custom view-based strategy; more info below), meaning that if a resource is POSTed with an uninitialized securable element, it will fail validation because it is a required field.

However, there is also a validation in the authorization layer that ensures these fields are initialized when creating and retrieving a resource. This validation might seem redundant, but it serves as an additional check in case some securable elements become nullable one day. The view-based strategy allows using nullable fields as securable elements, so the validation is not redundant in that scenario.

## What needs to be done in DMS

Unless specified in the remainder of this document, DMS will implement the same authorization design as ODS.

### Ownership-based authorization strategy

In DMS, there is a shared table where all resource entries are tracked (`dms.Document`), meaning that we don't need to add the `CreatedByOwnershipTokenId` to each resource root table as we did in ODS; we should add it to the `dms.Document` table as nullable and always populate it regardless of whether the `OwnershipBasedAuthorization` feature is enabled. We should also remove the feature flag entirely, since toggling its value does not require DB changes.

Storing the `CreatedByOwnershipTokenId` in `dms.Document` also means that we must join with `dms.Document` to authorize the resource entries.

### View-based authorization strategy

In DMS, we aim to apply joins using the DocumentId surrogate key instead of natural keys, meaning that the custom authorization views used by this strategy must output the DocumentId of the basis resource instead of the natural keys.

In the example above, the `auth.StudentWithCTECourseEnrollments` view will return the Student's DocumentId instead of the StudentUSI. See the `Resolving the DB columns used for authorization` section below for more information.

### Performance improvements over ODS

ODS executes an additional DB roundtrip for single-record authorizations, presumably because NHibernate limitations make batching difficult. In DMS, we have fine-grained control over the SQL queries we execute. Below are the expected DB roundtrips per operation.

#### PUT

- Roundtrip #1
  - Retrieve the DocumentId and etag by its Uuid (to abort if not found, and for reconstitution) (etag to enforce `If-Match`, if applicable)
- Roundtrip #2
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Run authorization check using the values from the request body (throw if unauthorized) (only if identifying values changed)
  - Retrieve the referenced resources' DocumentIds (using `dms.ReferentialIdentity`)
  - Reconstitute the record and/or materialize comparable current rowsets for no-op detection
- Roundtrip #3
  - Execute guarded no-op or update (only if it actually changed)

ODS requires at least 4 roundtrips for the same operation, and more if the resource has child tables.

#### POST

- Roundtrip #1
  - Retrieve the referenced resources' DocumentIds (using `dms.ReferentialIdentity`).
- Roundtrip #2
  - Retrieve the DocumentId and etag by its identifying values (to check if already exists, and for reconstitution) (etag to enforce `If-Match`, if applicable)
- Roundtrip #3
  - If a record with the same identifying values already exists:
    - Inline PUT Roundtrip #2 steps (excluding steps already executed)
  - Otherwise:
    - Run authorization check using the values from the request body (throw if unauthorized)
    - Insert into `dms.Document` and return its generated ID
- Roundtrip #4
  - Insert the resource tables, or inline PUT Roundtrip #3 steps

ODS requires 4 roundtrips for the same operation, and more if the resource has child tables.

#### DELETE

- Roundtrip #1
  - Retrieve the DocumentId and etag by its Uuid (to abort if not found) (etag to enforce `If-Match`, if applicable)
- Roundtrip #2
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Execute delete

ODS requires 3 roundtrips for the same operation.

#### GET-by-id

- Roundtrip #1
  - Retrieve the DocumentId and etag by its Uuid (to abort if not found, and for reconstitution) (etag to enforce `If-None-Match`, if applicable)
- Roundtrip #2
  - Run authorization check using the already-stored values (throw if unauthorized)
  - Reconstitute the record

ODS requires 2 roundtrips for the same operation.

#### GET-many

- Roundtrip #1
  - If filtering by Descriptor(s), convert DescriptorUris to DocumentIds. We could avoid this step by caching descriptors as in ODS.
- Roundtrip #2
  - Get the page's DocumentIds (apply authorization, filters, and offset/limit)
  - Get the TotalCount (if applicable)
- Roundtrip #3
  - Reconstitute the page

ODS requires 1 roundtrip for the same operation.

---

Even though the number of roundtrips above seems to be the same as in ODS, we should expect fewer roundtrips on average because DMS populates child tables in a single roundtrip.

We could further decrease the number of roundtrips if we implement the following measures:

- Cache the DescriptorUri-to-DocumentId mapping (similar to ODS)
- Change the reconstitution queries to use Uuid instead of DocumentId (requires joining with the Document table)
- Inline existence and ETag checks so they throw and abort the batch (similar to the auth checks)

NOTE: These counts do not include roundtrips related to authentication, which are typically served from the cache.

### Proof of concept

The C# program below showcases batching, how multiple strategies are combined, their execution order, and how problem details can be calculated.

Notice that auth checks are executed as early as possible (i.e. before reconstitution) to avoid spending compute resources on unauthorized requests.

```C#
using BatchedSqlTest;
using Npgsql;

await new ODS().RunTests();


await using var conn = new NpgsqlConnection("host=localhost;port=5432;username=postgres;database=EdFi_Ods_Sandbox_l54o9KBSGjVvrHuEUD6nh");
await conn.OpenAsync();

var token = (
    educationOrganizationIds: new[] { 255901L, 19255901L },
    namespacePrefixes: new[] { "uri://ed-fi.org", "uri://gbisd.edu" },
    ownershipTokens: new[] { 1 }
);

await GetManyCourses();
await PostCourseTranscript();
await GetGradeBookEntryById();

async Task GetManyCourses()
{
    // GET: /courses?limit=25&totalCount=true&courseCode=ALG-2

    // For this example, Course is configured with the authorization strategies:
    // - OwnershipBased
    // - RelationshipsWithEdOrgsAndPeople
    // - RelationshipsWithEdOrgsAndPeopleInverted

    // Omitted roundtrip #1: If filtering by Descriptor(s), convert DescriptorUris -> DocumentIds (using dms.ReferentialIdentity)

    var filterSql = @"
        SELECT edfi.Course.DocumentId
        FROM edfi.Course
        JOIN dms.Document ON dms.Document.DocumentId = edfi.Course.DocumentId     -- This join is only needed to authorize the CreatedByOwnershipTokenId
        WHERE
          dms.Document.CreatedByOwnershipTokenId = ANY(@OwnershipTokens)
          AND (
         EducationOrganizationId IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
         OR EducationOrganizationId IN (SELECT SourceEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE TargetEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
          )
          AND CourseCode = @CourseCode";

    await using var roundtrip2 = new NpgsqlCommand(@$"
        WITH filteredEntries AS ({filterSql})
        SELECT DocumentId FROM filteredEntries
        ORDER BY DocumentId
        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;

        WITH filteredEntries AS ({filterSql})
        SELECT COUNT(1) AS TotalCount FROM filteredEntries", conn);

    roundtrip2.Parameters.AddWithValue("OwnershipTokens", token.ownershipTokens);
    roundtrip2.Parameters.AddWithValue("TokenEducationOrganizationIds", token.educationOrganizationIds);
    roundtrip2.Parameters.AddWithValue("CourseCode", "ALG-2");
    roundtrip2.Parameters.AddWithValue("Offset", 0);
    roundtrip2.Parameters.AddWithValue("Limit", 25);

    await using var roundtrip2Reader = await roundtrip2.ExecuteReaderAsync();

    // Omitted roundtrip #3: Reconstitution queries

    while (await roundtrip2Reader.ReadAsync())
    {
        Console.WriteLine($"DocumentId: {roundtrip2Reader.GetInt64(roundtrip2Reader.GetOrdinal("DocumentId"))}");
    }

    await roundtrip2Reader.NextResultAsync();

    while (await roundtrip2Reader.ReadAsync())
    {
        Console.WriteLine($"TotalCount: {roundtrip2Reader.GetInt64(roundtrip2Reader.GetOrdinal("TotalCount"))}");
    }
}

async Task PostCourseTranscript()
{
    // POST: /courseTranscripts

    // For this example, CourseTranscript is configured with the authorization strategies:
    // - StudentWithCTECourseEnrollments (custom, view-based)
    // - OwnershipBased
    // - RelationshipsWithEdOrgsAndPeople

    var requestBody = new
    {
        courseAttemptResultDescriptor = "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
        courseReference = new
        {
            courseCode = "ART3-EM",
            educationOrganizationId = 255901001L,
        },
        studentAcademicRecordReference = new
        {
            educationOrganizationId = 255901001,
            schoolYear = 2022,
            studentUniqueId = "365155",
            termDescriptor = "uri://ed-fi.org/TermDescriptor#Spring Semester"
        }
    };

    // Omitted roundtrip #1: Retrieve referenced resources using dms.ReferentialIdentity, the next values are dummy
    var resolvedCourseAttemptResultDescriptorId = 12;
    var resolvedCourseDocumentId = 34;
    var resolvedStudentAcademicRecordDocumentId = 56;

    await using var roundtrip2 = new NpgsqlCommand(@"
        SELECT
          dms.Document.DocumentId,
          dms.Document.ContentVersion
        FROM edfi.CourseTranscript
        JOIN dms.Document ON dms.Document.DocumentId = edfi.CourseTranscript.DocumentId
        WHERE
          CourseAttemptResultDescriptor_DescriptorId = @CourseAttemptResultDescriptor_DescriptorId
          AND Course_DocumentId = @Course_DocumentId
          AND StudentAcademicRecord_DocumentId = @StudentAcademicRecord_DocumentId", conn);

    roundtrip2.Parameters.AddWithValue("CourseAttemptResultDescriptor_DescriptorId", resolvedCourseAttemptResultDescriptorId);
    roundtrip2.Parameters.AddWithValue("Course_DocumentId", resolvedCourseDocumentId);
    roundtrip2.Parameters.AddWithValue("StudentAcademicRecord_DocumentId", resolvedStudentAcademicRecordDocumentId);

    // Initialized only if the resource already exists (by its identifying values)
    long? documentId = null;
    long? contentVersion = null;

    await using (var roundtrip2Reader = await roundtrip2.ExecuteReaderAsync())
    {
        while (await roundtrip2Reader.ReadAsync())
        {
            documentId = roundtrip2Reader.GetInt64(roundtrip2Reader.GetOrdinal("DocumentId"));
            contentVersion = roundtrip2Reader.GetInt64(roundtrip2Reader.GetOrdinal("ContentVersion"));
        }
    }

    if (documentId == null)
    {
        // POST results in create

        await using var roundtrip3 = new NpgsqlCommand(@"
            -- Authorize request body values: view-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM StudentAcademicRecord
                    WHERE 
                        DocumentId = @StudentAcademicRecord_DocumentId
                        AND Student_DocumentId IN (SELECT DocumentId FROM auth.StudentWithCTECourseEnrollments)
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 0')
            END;

            -- NOTE: No need to authorize the OwnershipToken when creating an entry

            -- Authorize request body values: relationship-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    WHERE
                        @EducationOrganizationId IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
                        AND (SELECT Student_DocumentId FROM StudentAcademicRecord WHERE DocumentId = @StudentAcademicRecord_DocumentId)
                            IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 1')
            END;", conn);
        roundtrip3.Parameters.AddWithValue("StudentAcademicRecord_DocumentId", resolvedStudentAcademicRecordDocumentId);
        roundtrip3.Parameters.AddWithValue("EducationOrganizationId", requestBody.studentAcademicRecordReference.educationOrganizationId);
        roundtrip3.Parameters.AddWithValue("TokenEducationOrganizationIds", token.educationOrganizationIds);

        // Omitted command: Insert into `dms.Document` and return its generated ID

        try
        {
            await using var roundtrip3Reader = await roundtrip3.ExecuteReaderAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "AUTH1")
        {
            var authCheckIndex = int.Parse(ex.MessageText.Split("index: ")[1]);

            (string type, string detail, string[] errors) problemDetail = authCheckIndex switch
            {
                0 => ("urn:ed-fi:api:security:authorization", "Hint: You may need a Student with CTE Course Enrollments.", ["The caller is not authorized to perform the requested operation on the item based on the existing values of one or more of the following properties of the item: 'StudentUniqueId'."]),
                1 => ("urn:ed-fi:api:security:authorization", "Hint: You may need to create a corresponding 'StudentSchoolAssociation' item.", [$"No relationships have been established between the caller's education organization id claims ({string.Join(',', token.educationOrganizationIds)}) and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."]),
                _ => throw new InvalidOperationException()
            };
        }
    }
    else
    {
        // POST results in update

        await using var roundtrip3 = new NpgsqlCommand(@"
            -- Authorize stored values: view-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM edfi.CourseTranscript
                    JOIN edfi.StudentAcademicRecord ON StudentAcademicRecord.DocumentId = CourseTranscript.StudentAcademicRecord_DocumentId
                    WHERE
                        edfi.CourseTranscript.DocumentId = @DocumentId
                        AND StudentAcademicRecord.Student_DocumentId IN (SELECT DocumentId FROM auth.StudentWithCTECourseEnrollments)
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 0')
            END;

            -- Authorize stored values: ownership-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM edfi.CourseTranscript
                    JOIN dms.Document ON dms.Document.DocumentId = edfi.CourseTranscript.DocumentId
                    WHERE
                        edfi.CourseTranscript.DocumentId = @DocumentId
                        AND dms.Document.CreatedByOwnershipTokenId = ANY(@OwnershipTokens)
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 1')
            END;

            -- Authorize stored values: relationship-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM edfi.CourseTranscript
                    JOIN edfi.StudentAcademicRecord ON StudentAcademicRecord.DocumentId = CourseTranscript.StudentAcademicRecord_DocumentId
                    WHERE
                        edfi.CourseTranscript.DocumentId = @DocumentId
                        AND StudentAcademicRecord_EducationOrganizationId IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
                        AND StudentAcademicRecord.Student_DocumentId IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 2')
            END;

            -- The next authorization checks use the new values (from the request body), they are only needed if the identifying values changed

            -- Authorize request body values: view-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM StudentAcademicRecord
                    WHERE 
                        DocumentId = @NewStudentAcademicRecord_DocumentId
                        AND Student_DocumentId IN (SELECT DocumentId FROM auth.StudentWithCTECourseEnrollments)
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 3')
            END;

            -- NOTE: The CreatedByOwnershipTokenId cannot be changed by POST/PUT, so no ownership-based check needed

            -- Authorize request body values: relationship-based
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                        @NewEducationOrganizationId IN (SELECT TargetEducationOrganizationId FROM auth.EducationOrganizationIdToEducationOrganizationId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
                        AND (SELECT Student_DocumentId FROM StudentAcademicRecord WHERE DocumentId = @NewStudentAcademicRecord_DocumentId)
                            IN (SELECT Student_DocumentId FROM auth.EducationOrganizationIdToStudentDocumentId WHERE SourceEducationOrganizationId = ANY(@TokenEducationOrganizationIds))
                )
                THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 4')
            END;", conn);
        roundtrip3.Parameters.AddWithValue("DocumentId", documentId);
        roundtrip3.Parameters.AddWithValue("OwnershipTokens", token.ownershipTokens);
        roundtrip3.Parameters.AddWithValue("TokenEducationOrganizationIds", token.educationOrganizationIds);
        roundtrip3.Parameters.AddWithValue("NewStudentAcademicRecord_DocumentId", resolvedStudentAcademicRecordDocumentId);
        roundtrip3.Parameters.AddWithValue("NewEducationOrganizationId", requestBody.studentAcademicRecordReference.educationOrganizationId);

        // Omitted command: Retrieve referenced resources using dms.ReferentialIdentity
        // Omitted command: Reconstitution queries

        try
        {
            await using var roundtrip3Reader = await roundtrip3.ExecuteReaderAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "AUTH1")
        {
            var authCheckIndex = int.Parse(ex.MessageText.Split("index: ")[1]);

            (string type, string detail, string[] errors) problemDetail = authCheckIndex switch
            {
                0 or 3 => ("urn:ed-fi:api:security:authorization", "Hint: You may need a Student with CTE Course Enrollments.", ["The caller is not authorized to perform the requested operation on the item based on the existing values of one or more of the following properties of the item: 'StudentUniqueId'."]),
                1 => ("urn:ed-fi:api:security:authorization:ownership:access-denied:ownership-mismatch", "The item is not owned by the caller.", []),
                2 or 4 => ("urn:ed-fi:api:security:authorization", "Hint: You may need to create a corresponding 'StudentSchoolAssociation' item.", [$"No relationships have been established between the caller's education organization id claims ({string.Join(',', token.educationOrganizationIds)}) and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."]),
                _ => throw new InvalidOperationException()
            };
        }
    }

    // Omitted task: If update, calculate deltas

    // Omitted roundtrip #4: If create, insert the resource tables. If update, apply it
}

async Task GetGradeBookEntryById()
{
    // GET: /gradebookEntries/{id}

    // For this example, GradeBookEntry is configured with the authorization strategies:
    // - NamespaceBased

    var requestParams = new
    {
        uuid = new Guid("2af0e37e-9bb1-4770-8d0f-32d1b91c3984"),
        etag = 123
    };

    await using var roundtrip1 = new NpgsqlCommand(@"
        SELECT
          dms.Document.DocumentId,
          dms.Document.ContentVersion
        FROM edfi.GradeBookEntry
        JOIN dms.Document ON dms.Document.DocumentId = edfi.GradeBookEntry.DocumentId
        WHERE
          dms.Document.DocumentUuid = @uuid", conn);
    roundtrip1.Parameters.AddWithValue("uuid", requestParams.uuid);

    long? documentId = null;
    long? contentVersion = null;

    await using (var roundtrip1Reader = await roundtrip1.ExecuteReaderAsync())
    {
        while (await roundtrip1Reader.ReadAsync())
        {
            documentId = roundtrip1Reader.GetInt64(roundtrip1Reader.GetOrdinal("DocumentId"));
            contentVersion = roundtrip1Reader.GetInt64(roundtrip1Reader.GetOrdinal("ContentVersion"));
        }
    }

    if (documentId == null)
    {
        throw new InvalidOperationException(message: "Not found");
    }

    if (requestParams.etag == currentSerializedJsonEtag) // TBD Calculate the etag from the canonical serialized JSON representation
    {
        throw new InvalidOperationException(message: "Not modified");
    }

    await using var roundtrip2 = new NpgsqlCommand(@"
        -- Authorize stored values: namespace-based
        SELECT CASE
            WHEN EXISTS (
                SELECT 1
                FROM edfi.GradeBookEntry
                WHERE
                    DocumentId = @DocumentId
                    AND Namespace LIKE ANY (@NamespacePrefixes)
            )
            THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 0')
        END;", conn);
    roundtrip2.Parameters.AddWithValue("DocumentId", documentId);
    roundtrip2.Parameters.AddWithValue("NamespacePrefixes", token.namespacePrefixes.Select(n => $"{n}%").ToArray());

    // Omitted command: Reconstitution queries

    try
    {
        await using var roundtrip2Reader = await roundtrip2.ExecuteReaderAsync();
    }
    catch (PostgresException ex) when (ex.SqlState == "AUTH1")
    {
        var authCheckIndex = int.Parse(ex.MessageText.Split("index: ")[1]);

        (string type, string detail, string[] errors) problemDetail = authCheckIndex switch
        {
            0 => ("urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch", $"The existing 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('{token.namespacePrefixes[0]}', '{token.namespacePrefixes[1]}').", []),
            _ => throw new InvalidOperationException()
        };
    }
}
```

#### Error handling

In the `PostCourseTranscript` method in the POC above, an error is thrown when an authorization check fails, which aborts any remaining statements in the batch. This is an important design decision that allows us to include statements that would otherwise go in separate roundtrips. For example, the `dms.Document` entry is inserted after the auth check in the same batch.

The `AUTH1` error code is used when throwing authorization exceptions so that it can be caught in a try block in C#. The authorization check index is also included in the error description (for example: `Unauthorized, index: 0`). This way, when authorization errors occur they can be traced back to the specific authorization strategy that caused them, and the corresponding ProblemDetails can be generated. If necessary, additional roundtrips can provide further information in the ProblemDetails.

The generated ProblemDetails should follow the same structure defined in ODS. Refer to the following documents:

- [Error Response Knowledge Base](https://edfi.atlassian.net/wiki/spaces/ODSAPIS3V72/pages/56655873/Error+Response+Knowledge+Base#urn:ed-fi:api:security:authorization)
- [ODS-6285 Add hints to relationship-based authorization failure messages](https://edfi.atlassian.net/browse/ODS-6285)
- [ODS-6031 API error response for missing references related to authorization](https://edfi.atlassian.net/browse/ODS-6031)

#### Parameters and batching

Both SQL Server and PostgreSQL allow sending and executing dynamic Transact-SQL and PL/pgSQL, respectively. These languages support procedural logic (like `IF` and `THROW` statements). However, PostgreSQL has an important limitation: dynamic PL/pgSQL cannot be parameterized, meaning that we would need to construct the query using string concatenation, which is too problematic.

Because of this limitation, the POC stays as close as possible to standard SQL and relies on a custom `throw_error` function in PostgreSQL:

```sql
CREATE OR REPLACE FUNCTION dms.throw_error(code text, msg text)
RETURNS integer AS $$
BEGIN
    RAISE EXCEPTION '%', msg USING ERRCODE = code;
END;
$$ LANGUAGE plpgsql;
```

SQL Server doesn't allow throwing exceptions from custom scalar functions as done above. In SQL Server, we have the following options:

- Intentionally raise an invalid cast exception: `SELECT CAST('AUTH1 - Unauthorized, index: 0' AS INT)`. Although ugly, it easily substitutes the `throw_error` function, making both PgSQL and MsSql queries similar. This is the recommended approach.
- Use Transact-SQL (which supports parameterization) with `IF` and `THROW` statements. The main downside is that the resulting SQL would be very different from PgSQL.

#### Sub-queries instead of joins

Notice how ODS joins against the authorization views, whereas the POC above uses an `IN` subquery.

ODS has to use `DISTINCT` to ensure that multiple entries in the auth views don't result in duplicate rows during GET-many. Avoiding the `DISTINCT` clause results in simpler execution plans and performance improvements.

#### Resolving the DB columns used for authorization

We should avoid joining the people auth views against the resource tables using UniqueIds, as they are nvarchar(32). ODS joins using USIs (bigint); the DMS equivalent is the DocumentId, meaning that auth views that used to return USIs should now return DocumentIds.

However, the person DocumentId column is only available on the resource table when it references the person resource *directly*. `CourseTranscript`, for example, references `Student` transitively through `StudentAcademicRecord`, so the Student DocumentId column isn't available in the `CourseTranscript` table. We must join `StudentAcademicRecord` to reach the Student DocumentId in order to authorize it (as shown in the POC above).

The `Namespace` and `EducationOrganizationId` columns are simpler to get since they are always available in the resource being authorized; no joining is needed.

We need a helper function that, given the resource that we are trying to authorize, returns the necessary information to construct the SQL authorization check.

**ResolveSecurableElementColumnPath(subjectResourceFullName, securableElement)**

- The `subjectResourceFullName` parameter gets initialized with the `ResourceName` from the ApiSchema.json, plus its project name
- The `securableElement` parameter gets initialized with the securable element from the ApiSchema.json, for example:

  ```json
  {    
    "Student": [
      "$.studentAcademicRecordReference.studentUniqueId"
    ]
  }
  ```

For the `CourseTranscript` example, the function should return the following collection:

  ```json
  [
    {
      "sourceTable": {
        "schema": "edfi",
        "name": "CourseTranscript"
      },
      "sourceColumnName": "StudentAcademicRecord_DocumentId",
      "targetTable": {
        "schema": "edfi",
        "name": "StudentAcademicRecord"
      },
      "targetColumnName": "DocumentId"
    },
    {
      "sourceTable": {
        "schema": "edfi",
        "name": "StudentAcademicRecord"
      },
      "sourceColumnName": "Student_DocumentId",
      "targetTable": {
        "schema": "edfi",
        "name": "Student"
      },
      "targetColumnName": "DocumentId"
    },
  ]
  ```

The high-level logic is as follows:

1. For each given securable element path, look in the `documentPathsMapping` of the resource for an entry where the securable element *path* matches the `referenceJsonPath`
2. Take the `resourceName` from the matching entry from the previous step, get its securable element, and repeat the process until it reaches the top (i.e. until it has reached the Student/Staff/Contact resource)
3. Use the Derived Relational Model to calculate the tables and columns needed to build the result

Note that a securableElement might have multiple paths when key unification takes place. In this situation, the function should follow each path and pick the shortest one to minimize the number of joins. Use the canonical column instead of the alias, since the canonical column will be indexed.

When the provided securableElement is a `Namespace` or an `EducationOrganization`, it should extract the column name directly (no need to visit references) because those are always available on the root resource table.

For example if the `securableElement` is:

```json
{
  "EducationOrganization": [
    {
      "jsonPath": "$.studentEducationOrganizationAssociationReference.educationOrganizationId",
      "metaEdName": "EducationOrganizationId"
    }
  ]
}
```

The helper function should return:

```json
[
  {
    "sourceTable": {
      "schema": "edfi",
      "name": "StudentAssessmentRegistration"
    },
    "sourceColumnName": "StudentEducationOrganizationAssociation_EducationOrganizationId",
    "targetTable": null,
    "targetColumnName": null
  }
]
```

Similarly, if the `securableElement` is:

```json
{
  "Namespace": [
    "$.assessmentAdministrationReference.namespace"
  ]
}
```

The helper function should return:

```json
[
  {
    "sourceTable": {
      "schema": "edfi",
      "name": "StudentAssessmentRegistration"
    },
    "sourceColumnName": "AssessmentAdministration_Namespace",
    "targetTable": null,
    "targetColumnName": null
  }
]
```

---

In the View-based authorization strategy, the basis resource *is* the securableElement, meaning that we need a similar helper function that takes the subject and the basis resource.

**ResolveSecurableElementColumnPath(subjectResourceFullName, basisResourceFullName)**

- The `subjectResourceFullName` parameter gets initialized with the `ResourceName` from the ApiSchema.json, plus its project name
- The `basisResourceFullName` parameter gets initialized with the `ResourceName` from the ApiSchema.json, plus its project name
- The returned value is the same as above

The high-level logic is as follows: using ApiSchema.json's `documentPathsMapping`, recursively traverse all the references from the subjectResource and take note of those that reach the basisResource. 

There can be multiple resulting paths; pick the winner based on:

1. Part-of-identity references win over non-part-of-identity references
2. Required references win over optional references
3. Non-role-named references win over role-named references
4. Shortest path wins

Note that non-part-of-identity references are only allowed in the subjectResource (not in the middle of the reference chain).

The basisResource can also be a descriptor. Assume that the custom view `TransportationTypeDescriptorWithABus` is assigned to the `StudentTransportation` resource; the helper function should join with the `dms.Descriptor` table and return:
```json
[
  {
    "sourceTable": {
      "schema": "edfi",
      "name": "StudentTransportation"
    },
    "sourceColumnName": "TransportationTypeDescriptor_DescriptorId",
    "targetTable": {
      "schema": "dms",
      "name": "Descriptor"
    },
    "targetColumnName": "DocumentId"
  }
]
```
Custom views that return descriptors (such as the `TransportationTypeDescriptorWithABus` above) should return the descriptor's DocumentId as they appear in `dms.Descriptor.DocumentId`.

This function overload should also allow passing an abstract resource (such as `EducationOrganization` or `GeneralStudentProgramAssociation`) as the basis resource.

Tests cover the following scenarios:
  - The Basis resource is a directly referenced descriptor
    - Assign `TransportationTypeDescriptorWithABus` to `StudentTransportation`
  - The Basis resource is an indirectly referenced descriptor
  - The Basis resource is a directly referenced resource
  - The Basis resource is an indirectly referenced resource
  - The Basis resource is the subject resource (self-reference)
    - Assign `StudentWithCTECourseEnrollments` to `Student`
    - Assign `TransportationTypeDescriptorWithABus` to `TransportationTypeDescriptor`
  - The basis resource is abstract
    - Assign `EducationOrganizationWithACategoryContainingAnSWord` to `School` (self-reference)
    - Assign `EducationOrganizationWithACategoryContainingAnSWord` to `BellSchedule`
    - Assign `EducationOrganizationWithACategoryContainingAnSWord` to `StudentSchoolAssociation`. It should use `StudentSchoolAssociation.GraduationPlan.EducationOrganizationId` instead of `StudentSchoolAssociation.SchoolId`.
  - The basis resource is concrete
    - Assign `SchoolContainingAnSWord` to `StaffSchoolAssociation`
    - Assign `SchoolContainingAnSWord` to `Intervention`. Note that `Intervention` references the abstract Education Organization (instead of the concrete School). This isn't supported by ODS but we should support it for consistency.

#### TVPs in SQL Server

There are a few SQL queries that must filter based on a list:

1. Filter `auth.EducationOrganizationIdToEducationOrganizationId` based on the EdOrgIds defined in the token
2. Filter the resource's Namespace based on the prefixes defined in the token
3. In the GET-many scenario, filter the page by the authorized DocumentIds
4. When retrieving the referenced resources DocumentIds using the `dms.ReferentialIdentity` table, we need to filter by a list of ReferentialIds
5. Filter the resource's `CreatedByOwnershipTokenId` by the ownership tokens configured in the token

PostgreSQL allows sending arrays as parameters (as shown in the POC above); the equivalent in SQL Server is Table-Valued Parameters (TVPs). Note that TVPs seem to degrade performance as reported [here](https://dba.stackexchange.com/a/344923).

When the list has fewer than 2,000 records, ODS uses `IN(@p1, @p2, @p3...)`. Otherwise, it uses TVPs to avoid hitting the parameter limit and presumably to improve performance.

DMS should follow a similar approach for SQL Server: when any of the lists mentioned above have fewer than 2,000 records, it should fall back to a parameterized `IN` clause (or an `OR` clause for Namespace prefixes) and use TVPs when the list has 2,000 or more entries.

This means that the DDL generator has to create the following User-Defined Table Types:

- Table of bigint (covers point 1. and 3. from the list above)
- Table of uniqueidentifier (covers point 4.)

```sql
CREATE TYPE dms.BigIntTable AS TABLE(
  Id BIGINT NOT NULL
);

CREATE TYPE dms.UniqueIdentifierTable AS TABLE(
 Id uniqueidentifier NOT NULL
);
```

Note that for Namespace prefixes and Ownership tokens, we won't use TVPs; we will throw an error whenever the token has 2,000 or more prefixes/ownership tokens.

#### What is missing from the POC

The DELETE operation isn't shown in the POC, as it would be very similar to the `GetGradeBookEntryById` example but deletes the entry instead of reconstituting it.

PUT would be a combination of what's shown in `PostCourseTranscript` and `GetGradeBookEntryById`, see the roundtrips specification above for details.

When creating or updating an entry configured with the Namespace-based strategy, we should authorize using a SQL similar to:

```sql
-- Authorize request body values: namespace-based
SELECT CASE
    WHEN EXISTS (
        SELECT 1
        WHERE 'uri://ed-fi.org/GradebookEntry/GradebookEntry.xml' LIKE ANY (ARRAY[
            'uri://ed-fi.org%',
            'uri://gbisd.edu%'
        ])
    )
    THEN 1 ELSE throw_error('AUTH1', 'Unauthorized, index: 0')
END;
```

The example above is illustrative; the actual implementation should parameterize the namespaces.

Note that ODS does this check in C# before hitting the DB. We will do it in SQL to keep it simple and consistent with the other authorization strategies. We can move this check to C# post v1.0 if performance tests show that SQL is too expensive.

The `*Relationships` strategies below aren't shown in the POC as they are very similar to the `RelationshipsWithEdOrgsAndPeople` and `RelationshipsWithEdOrgsAndPeopleInverted` strategies:

- RelationshipsWithEdOrgsOnly
- RelationshipsWithEdOrgsOnlyInverted
- RelationshipsWithPeopleOnly
- RelationshipsWithStudentsOnly
- RelationshipsWithStudentsOnlyThroughResponsibility

The `NoFurtherAuthorizationRequired` strategy isn't shown since it simply grants access after authentication checks succeeded.

#### Further performance improvements

There are performance optimizations that we could implement for specific scenarios. These are kept out of scope to prioritize simplicity. If we identify bottlenecks during performance tests, we can consider implementing the following optimizations.

- Don't execute the `Authorize request body values` step if the identifying values didn't change (on POST/PUT)
- Some Namespace and Ownership checks can be done in C#, and if unauthorized, reject the request before hitting the DB
- If the resource's EducationOrganizationId appears directly in the client's token, we can grant access without generating the SQL check
- Update the bulk reference resolution logic to also resolve people's DocumentIds that are referenced either directly or transitively. This would avoid the joins on the POST/PUT `Authorize request body values` step.
- Convert the authorization views from *normal* views to Indexed Views (only applicable for SQL Server)

### SQL generation and AOT

Caching resource-specific SQL checks would require the cache key to include at least the following fields:
- EffectiveSchemaHash 
- Resource 
- Operation (create, update, delete, read-single, read-many)
- Applied authorization strategies (with `AND` strategies ordering)
- How many EdOrgIds, Namespace prefixes, and Ownership tokens were rendered (because SQL Server uses `IN (@param1, @param2, ...)` and TVPs)
- SecurableElement

The resulting cache key would be too specific, causing frequent cache misses and a cache that could become too large. Additionally, if we forget to include fields in the cache key, bugs with high impact arise.

Because of this, caching resource-specific SQL checks is **discouraged**.  However, we should cache calculations that aid during SQL check generation, for example, the calls to the `ResolveSecurableElementColumnPath` function.

The [aot-compilation.md](aot-compilation.md) document says that SQL should be pre-computed and stored in .mpac files. This is out of scope because of the reasons above. Also, custom view-based checks cannot be pre-computed as they can be defined and configured after the ApiSchema.json has been generated.

### Database Model

#### Education Organization Hierarchy

The `auth.EducationOrganizationIdToEducationOrganizationId` will remain the same as in ODS.

**PostgreSQL**

```sql
CREATE TABLE IF NOT EXISTS auth.EducationOrganizationIdToEducationOrganizationId
(
    SourceEducationOrganizationId bigint NOT NULL,
    TargetEducationOrganizationId bigint NOT NULL,
    CONSTRAINT EducationOrganizationIdToEducationOrganizationId_PK PRIMARY KEY (SourceEducationOrganizationId, TargetEducationOrganizationId)
)
```

The `*Inverted` authorization strategies benefit from the following index:

```sql
CREATE INDEX IX_EducationOrganizationIdToEducationOrganizationId ON auth.EducationOrganizationIdToEducationOrganizationId
(
 TargetEducationOrganizationId
) INCLUDE (SourceEducationOrganizationId);
```

We also need to bring the same triggers that update the Education Organization hierarchy, [defined in ODS here](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/1302-CreateEdOrgToEdOrgTriggers.sql). These triggers should use denormalized EdOrgId columns (do not join with the EducationOrganization table), use the *unified* columns when available (i.e. we should use the *stored* columns).

To avoid triggers, we could maintain the `auth.EducationOrganizationIdToEducationOrganizationId` table from C#. We will start with triggers similar to ODS for DMS v1.0 to save development time and migrate them to C# post v1.0 if necessary. At first glance, these triggers do not appear to be phantom-safe. Education Organizations likely change so rarely that phantoms are unlikely to occur in practice. However, if we migrate the triggers to C#, we should account for phantoms and consider introducing a locking table, because performing this logic in C# adds latency due to DB roundtrips.

#### People auth views

We have to bring the following people auth views from ODS:

- [EducationOrganizationIdToStudentDocumentId](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/1303-AuthViewEducationOrganizationIdToStudentUSI.sql)
- [EducationOrganizationIdToContactDocumentId](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/1304-AuthViewEducationOrganizationIdToContactUSI.sql)
- [EducationOrganizationIdToStaffDocumentId](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/1305-AuthViewsEducationOrganizationIdToStaffUSI.sql)
- [EducationOrganizationIdToStudentDocumentIdThroughResponsibility](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/1306-AuthViewEducationOrganizationIdToStudentUSIThroughResponsibility.sql)

In DMS, these views should output the DocumentId instead of the USI (for example, `Student_DocumentId` instead of `StudentUSI`). For clarity, we should add the person type name as a prefix to the `DocumentId` column.

Given that people types are rarely added or modified (in the DS or extensions) and their definitions are not easily generalizable (Staff joins against two association tables; Contact goes through Student), the view definitions should be *hard-coded*.

The views are only emitted when all five dependent association resources (`StudentSchoolAssociation`, `StudentContactAssociation`, `StaffEducationOrganizationAssignmentAssociation`, `StaffEducationOrganizationEmploymentAssociation`, `StudentEducationOrganizationResponsibilityAssociation`) are present in the derived relational model. This guard exists because synthetic or partial test models may not include the association resources that the views join against. In any full DS 5.2 deployment these associations are always present, so the guard is never triggered in production.

#### Indexes

The following indexes are needed to run the authorization checks efficiently.
NOTE: Index the canonical columns (when available) as alias columns cannot be indexed.

**Ownership-based strategy**
There should be an index on the `dms.Document.CreatedByOwnershipTokenId` column.

**Namespace-based strategy**
Resources that have a `Namespace` securableElement should have an index on the corresponding column (use the Derived Relational Model to map from the securable element path to the DB column).

**Relationship-based strategies**
The `auth.EducationOrganizationIdToEducationOrganizationId` table should have the following indexes:

- `SourceEducationOrganizationId`, include `TargetEducationOrganizationId`
- `TargetEducationOrganizationId`, include `SourceEducationOrganizationId`

PrimaryAssociations should have the following indexes:

- `edfi.StudentSchoolAssociation` should have an index on the `SchoolId` column, include the `Student_DocumentId`
- `edfi.StudentContactAssociation` should have an index on the `Student_DocumentId` column, include the `Contact_DocumentId`
- `edfi.StaffEducationOrganizationAssignmentAssociation` should have an index on the `EducationOrganization_EducationOrganizationId` column, include the `Staff_DocumentId`
- `edfi.StaffEducationOrganizationEmploymentAssociation` should have an index on the `EducationOrganization_EducationOrganizationId` column, include the `Staff_DocumentId`
- `edfi.StudentEducationOrganizationResponsibilityAssociation` should have an index on the `EducationOrganization_EducationOrganizationId` column, include the `Student_DocumentId`

Resources that have an EducationOrganization securableElement should have an index on the corresponding column (use the Derived Relational Model to map from the securable element path to the DB column). Do not create the index if it is already covered in the list above.

There should be an index on all resources that participate in a person join (see the `Resolving the DB columns used for authorization` section above). For example, `CourseTranscript` references `StudentAcademicRecord`, which references `Student` meaning that there should be an index on the following columns:

- `edfi.CourseTranscript` should have an index on the `StudentAcademicRecord_DocumentId` column, include its own `DocumentId`
- `edfi.StudentAcademicRecord` should have an index on the `Student_DocumentId` column, include its own `DocumentId`

**View-based strategy**
Given that view-based views are created after MetaEd has generated the ApiSchema.json, there is no way to know what fields need to be indexed beforehand (other than indexing every possible reference), so implementers are responsible for creating the necessary indexes.

Indexing in ODS was relatively simple because it uses natural keys, so all the columns that participate in authorization decisions are always available in the resource being authorized. In DMS, the DocumentIds used for authorization aren't always available in the resource (as shown in `CourseTranscript`), meaning that implementers need to figure out what tables and columns participate in the join in order to create the necessary indexes.

One option is to create a tool that analyzes the current authorization metadata and outputs the necessary indexes. This is out of the scope of this design.

#### Fingerprinting
These database objects should be fingerprinted as part of the full emitted SQL text that gets fingerprinted as a unit by the `DdlManifestEmitter`.

### ProblemDetails

DMS must return the same ProblemDetails structure as ODS when authorization fails. This section summarizes authorization-related ProblemDetails, their expected error messages, and hints.

The response implements the [Problem Details RFC 9457](https://www.rfc-editor.org/rfc/rfc9457.html), an explanation of each field is:

- `type`: Uniquely identifies the error type as specified in RFC 9457. `type` is defined as a URI where each segment represents a level in the hierarchy into which the error types are organized. For example, `urn:ed-fi:api:security:authorization:access-denied:resource` and `urn:ed-fi:api:security:authorization:access-denied:action` identify specific issue types within the context of an authorization error.
- `title`: A user-friendly representation of the `type`.
- `detail`: A user-friendly description of the encountered issue.
- `errors`: Sometimes additional details are provided in the `errors` extension member. This allows for supplementary descriptions aimed at API client developers and API hosts to facilitate the identification and resolution of errors.
- `correlationId`: This field allows traceability of the specific occurrence of the error and connects the error response to entries in the API error logs.

Many of the errors below show the securable element to the end user. To make it user-friendly, the securable element path is split by `.`, the last element is taken, and its first letter is uppercased (referred to below as the `ReadableSecurableElement`). For example, the `StudentSchoolAssociationResource` has the following securable elements:

```json
{
  "securableElements": {
    "EducationOrganization": [
      {
        "jsonPath": "$.schoolReference.schoolId",
        "metaEdName": "SchoolId"
      }
    ],
    "Student": [
      "$.studentReference.studentUniqueId"
    ]
  }
}
```

Which results in the following example ProblemDetail:

```json
{
  "detail": "Access to the requested data could not be authorized. Hint: You may need to create a corresponding 'StudentSchoolAssociation' item.",
  "type": "urn:ed-fi:api:security:authorization",
  "title": "Authorization Denied",
  "status": 403,
  "correlationId": "9770a449-0bf0-4104-8c23-40fba4fe9326",
  "errors": [
    "No relationships have been established between the caller's education organization id claims (none) and one or more of the following properties of the resource item: 'SchoolId', 'StudentUniqueId'."
  ]
}
```

Notice how the securable element paths got translated:

- `$.schoolReference.schoolId` became `SchoolId`
- `$.studentReference.studentUniqueId` became `StudentUniqueId`

#### 1. Authentication Failures (401 Unauthorized)

**Type**: `urn:ed-fi:api:security:authentication`

**Title**: `Authentication Failed`

**Status**: `401`

**Detail**: `The caller could not be authenticated.`

| Scenario | Error |
|---|---|
| No `Authorization` header provided | `Authorization header is missing.` |
| Unrecognized authentication scheme (not `Bearer`) | `Unknown Authorization header scheme.` |
| `Bearer` scheme present but token value is empty | `Missing Authorization header bearer token value.` |
| Malformed or unparseable `Authorization` header | `Invalid Authorization header.` |
| Token not found or expired | `Invalid token` |

#### 2. Authorization Denied (403 Forbidden)

**Type**: `urn:ed-fi:api:security:authorization` (with additional sub-type parts appended per scenario)

**Title**: `Authorization Denied`

**Status**: `403`

**Default Detail**: `Access to the requested data could not be authorized.`

##### 2.3. Relationship-based — No relationships established (with EdOrg claims)

The view-based authorization check found no relationship between the caller's education organization IDs and the resource item's securable elements.

**Type**: `urn:ed-fi:api:security:authorization`

When a **single** securable element is involved:

**Detail**: `Access to the requested data could not be authorized.` (with optional `Hint: ...` appended, see hints table below)

**Error**: `No relationships have been established between the caller's education organization id {claim/claims} ({edOrgId1}, {edOrgId2}, ...) and the resource item's '{ReadableSecurableElement}' value.`

When **multiple** securable elements are involved:

**Error**: `No relationships have been established between the caller's education organization id {claim/claims} ({edOrgId1}, {edOrgId2}, ...) and one or more of the following properties of the resource item: '{ReadableSecurableElement1}', '{ReadableSecurableElement2}'.`

> Note: If there are more than 5 EdOrg claim values, only the first 5 are shown followed by `...`.

##### 2.4. Relationship-based / Custom view — No relationships established (without EdOrg claims)

Custom view-based authorization checks may not involve EdOrg claims. In this case, the error message uses a different format.

**Type**: `urn:ed-fi:api:security:authorization`

When a **single** securable element is involved:

**Detail**: `Access to the requested data could not be authorized.` (with optional `Hint: ...` appended)

**Error**: `The caller is not authorized to perform the requested operation on the item based on the {existing/proposed} value of the '{ReadableSecurableElement}' property of the item.`

When **multiple** securable elements are involved:

**Error**: `The caller is not authorized to perform the requested operation on the item based on the {existing/proposed} values of one or more of the following properties of the item: '{ReadableSecurableElement1}', '{ReadableSecurableElement2}'.`

##### Authorization Failure Hints

When a view-based authorization check fails, ODS appends a hint to the `detail` field based on the authorization views that were used. DMS should produce the same hints.

| Authorization View | Hint |
|---|---|
| `EducationOrganizationIdToStudentDocumentId` | `You may need to create a corresponding 'StudentSchoolAssociation' item.` |
| `EducationOrganizationIdToContactDocumentId` | `You may need to create corresponding 'StudentSchoolAssociation' and 'StudentContactAssociation' items.` |
| `EducationOrganizationIdToStaffDocumentId` | `You may need to create corresponding 'StaffEducationOrganizationEmploymentAssociation' or 'StaffEducationOrganizationAssignmentAssociation' items.` |
| `EducationOrganizationIdToStudentDocumentIdThroughResponsibility` | `You may need to create a corresponding 'StudentEducationOrganizationResponsibilityAssociation' item.` |
| Custom view (e.g. `StudentWithCTECourseEnrollments`) | `You may need {a/an} {Display Text}.` (e.g., `You may need a Student with CTE Course Enrollments.`) |

The hint is formatted as:

**Detail**: `Access to the requested data could not be authorized. Hint: {hint text}`

If multiple distinct hints apply, they are concatenated using a space as separator. For example: 
```
Access to the requested data could not be authorized. Hint: You may need to create a corresponding 'StudentSchoolAssociation' item. You may need to create a corresponding 'StudentEducationOrganizationResponsibilityAssociation' item.
```

##### 2.5. Relationship-based — Required element uninitialized (existing data)

The resource item already stored in the DB has a null value for a field that is required for authorization, making it inaccessible.

**Type**: `urn:ed-fi:api:security:authorization:relationships:invalid-data:element-uninitialized`

**Detail**: `Access to the requested data could not be authorized. The existing '{ReadableSecurableElement}' value is required for authorization purposes.`

**Error**: `The existing resource item is inaccessible to clients using the '{authorizationStrategyName}' authorization strategy.`

##### 2.6. Relationship-based — Required element missing (proposed data)

The request body is missing a value for a field that is required for authorization.

**Type**: `urn:ed-fi:api:security:authorization:relationships:access-denied:element-required`

**Detail**: `Access to the requested data could not be authorized. The '{ReadableSecurableElement}' value is required for authorization purposes.`

**Error**: *(empty)*

##### 2.7. Custom view — Required element uninitialized (existing data)

Same as 2.5 but for custom view-based authorization.

**Type**: `urn:ed-fi:api:security:authorization:custom-view:invalid-data:element-uninitialized`

**Detail**: `Access to the requested data could not be authorized. The existing '{ReadableSecurableElement}' value is required for authorization purposes.`

**Error**: `The existing resource item is inaccessible to clients using the '{authorizationStrategyName}' authorization strategy.`

##### 2.8. Custom view — Required element missing (proposed data)

Same as 2.6 but for custom view-based authorization.

**Type**: `urn:ed-fi:api:security:authorization:custom-view:access-denied:element-required`

**Detail**: `Access to the requested data could not be authorized. The '{ReadableSecurableElement}' value is required for authorization purposes.`

**Error**: *(empty)*

##### 2.9. Namespace-based — No namespace prefixes configured on the API client

The API client has been assigned a resource that uses namespace-based authorization, but the client has no namespace prefixes assigned.

**Type**: `urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces`

**Detail**: `There was a problem authorizing the request. The caller has not been configured correctly for accessing resources authorized by Namespace.`

**Error**: `The API client has been given permissions on a resource that uses the '{authorizationStrategyName}' authorization strategy but the client doesn't have any namespace prefixes assigned.`

##### 2.10. Namespace-based — Namespace value uninitialized (existing data)

The stored resource has a null or empty `Namespace` value.

**Type**: `urn:ed-fi:api:security:authorization:namespace:invalid-data:namespace-uninitialized`

**Detail**: `Access to the requested data could not be authorized. The existing 'Namespace' value has not been assigned but is required for authorization purposes.`

**Error**: `The existing resource item is inaccessible to clients using the '{authorizationStrategyName}' authorization strategy because the 'Namespace' value has not been assigned.`

##### 2.11. Namespace-based — Namespace value missing (proposed data)

The request body has a null or empty `Namespace` value.

**Type**: `urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-required`

**Detail**: `Access to the requested data could not be authorized. The 'Namespace' value has not been assigned but is required for authorization purposes.`

**Error**: *(empty)*

##### 2.12. Namespace-based — Namespace mismatch

The resource's namespace does not start with any of the caller's assigned namespace prefixes.

**Type**: `urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch`

**Detail**: `Access to the requested data could not be authorized. The {existing }'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('{prefix1}', '{prefix2}').`

**Error**: *(empty)*

> Note: The word `existing` is included in the detail only when authorizing against stored (existing) data, not when authorizing proposed (request body) data.

##### 2.13. Ownership-based — Ownership token mismatch

The resource's `CreatedByOwnershipTokenId` does not match any of the caller's ownership tokens.

**Type**: `urn:ed-fi:api:security:authorization:ownership:access-denied:ownership-mismatch`

**Detail**: `Access to the requested data could not be authorized. The item is not owned by the caller.`

**Error**: *(empty)*

##### 2.14. Ownership-based — Ownership token uninitialized

The stored resource has a null `CreatedByOwnershipTokenId`, making it permanently inaccessible via ownership-based authorization.

**Type**: `urn:ed-fi:api:security:authorization:ownership:invalid-data:ownership-uninitialized`

**Detail**: `Access to the requested data could not be authorized. The item is not owned by the caller.`

**Error**: `The existing resource item has no 'CreatedByOwnershipTokenId' value assigned and thus will never be accessible to clients using the '{authorizationStrategyName}' authorization strategy.`

#### 4. Security Configuration Errors (500 Internal Server Error)

These indicate a misconfiguration in the security metadata and should not occur under normal conditions. DMS should return these when the authorization metadata is inconsistent or incomplete.

**Type**: `urn:ed-fi:api:system-configuration:security`

**Title**: `Security Configuration Error`

**Status**: `500`

**Detail**: `A security configuration problem was detected. The request cannot be authorized.`

| Scenario | Error |
|---|---|
| Resource has no security metadata | `No security metadata has been configured for this resource.` |
| No authorization strategies defined for the matching claim | `No authorization strategies were defined for the requested action '{action}' against resource URIs ['{uri1}', '{uri2}'] matched by the caller's claim '{claimName}'.` |
| Authorization strategy implementation not found | `Could not find authorization strategy implementations for the following strategy names: '{strategyName1}', '{strategyName2}'.` |
| Custom view basis entity property not found on the target entity | `Unable to find a property on the authorization subject entity type '{targetEntityName}' corresponding to the '{propertyName}' property on the custom authorization view's basis entity type '{basisEntityName}' in order to perform authorization. Should a different authorization strategy be used?` |

### Extensions

Resources that are created in extensions (such as `tpdm.Candidate`) are authorized in the same way as core resources; MetaEd already identifies them and initializes the corresponding securableElements in their ApiSchema.json.

Authorization cannot be applied on fields added to core resources (such as `edfi.Credential._ext.tpdm.certificationRouteDescriptor`) as these fields do not qualify as securableElements.

### Improve batch caching by using NpgsqlBatch

The POC above builds a single (large) command composed of many statements. PostgreSQL caches the plan and reuses it if it sees another command that is *exactly* the same.

The likelihood that another command is exactly the same is not as high because of the authorization queries that can vary between tokens. To improve plan reusability, we could use [NpgsqlBatch](https://www.npgsql.org/doc/api/Npgsql.NpgsqlBatch.html), which allows breaking the large command into multiple smaller commands that would each be cached independently.

### Authentication

DMS uses JWT Bearer tokens validated against an OpenID Connect (OIDC) identity provider. The identity provider is either the Configuration Service (via OpenIddict) or Keycloak.

The following auth metadata is stored in the Configuration Service (CMS):
- Claim Sets (i.e., what strategies apply for a given endpoint, and the order in which `AND` strategies execute).
  - Cached during DMS startup, TTL is 600s by default, configurable in appsettings.json: `CacheSettings.ClaimSetsCacheExpirationSeconds`
- Granted EducationOrganizationIds, Namespace prefixes, and Ownership tokens.
  - These get encoded in the JWT during token generation, token TTL is 30m by default, configurable in CMS appsettings.json `IdentitySettings.TokenExpirationMinutes`

DMS's current authentication implementation is mostly unaffected by this redesign.

### Row-level security

Both SQL Server and PostgreSQL support row-level security; however, the recommendation is not to use it for DMS v1.0 given the short development timeline and the uncertainty surrounding the feature. If we adopt it and it turns out to have show-stopping limitations or unacceptable performance, it could jeopardize the release.

### Out of scope

- ChangeQueries and the related `*IncludingDeletes` authorization strategies are out of scope. They will be covered in a future spike.
- Automatically discovering new person types in extensions is out of scope, given that it is an unlikely scenario.
- DS 5.2 switched from `Parent` to `Contact`, meaning that supporting DS 4 and below requires additional logic, which is out of scope.
