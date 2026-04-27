// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlDeleteByIdAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlDeleteByIdNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Relational_Delete_By_Id
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Morning" }] }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
              "addresses": [{ "_ext": { "sample": { "zone": "Zone-1" } } }],
              "interventions": [
                { "interventionCode": "Attendance", "visits": [{ "visitCode": "Visit-A" }] }
              ]
            }
          }
        }
        """;

    private static readonly ResourceInfo _schoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    private static readonly ResourceInfo _unrelatedResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Program"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private RecordingLogger<RelationalDocumentStoreRepository> _recordingLogger = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        // SQL Server's "DBCC CHECKIDENT(..., RESEED, 0)" (used by MssqlDatabaseResetSql) makes the
        // next INSERT use 0 on a table that has never been populated — but RESEED N + 1 on a table
        // that has ever been populated. Issue a single insert-then-delete against dms.Document so
        // the identity counter is "activated"; subsequent ResetAsync() calls then correctly yield
        // next-value = 1 on an empty table instead of 0 (which would otherwise fail
        // DefaultRelationalWriteExecutor.ValidatePersistedTargetIdentity with DocumentId == 0).
        var resourceKeyId = _mappingSet.ResourceKeyIdByResource[new QualifiedResourceName("Ed-Fi", "School")];
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            VALUES (@documentUuid, @resourceKeyId);

            DELETE FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", Guid.NewGuid()),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        _recordingLogger = new RecordingLogger<RelationalDocumentStoreRepository>();
        _serviceProvider = CreateServiceProvider(_recordingLogger);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_removes_the_document_and_cascades_child_rows_on_delete_success()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("eeeeeeee-0000-0000-0000-000000000001"));

        var upsert = await InvokeAsync(repository =>
            repository.UpsertDocument(CreateUpsertRequest(documentUuid))
        );
        upsert.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Capture DocumentId before delete; counting cascaded rows by DocumentId (rather than
        // joining child tables back to dms.Document on DocumentUuid) guarantees the post-delete
        // assertions are not trivially satisfied by the Document row already being gone.
        var documentId = await GetDocumentIdAsync(documentUuid);
        documentId.Should().NotBeNull();
        (await CountDocumentsAsync(documentUuid)).Should().Be(1);
        (await CountSchoolRootRowsAsync(documentId!.Value)).Should().Be(1);
        (await CountSchoolAddressRowsAsync(documentId.Value)).Should().BeGreaterThan(0);
        (await CountSchoolAddressPeriodRowsAsync(documentId.Value)).Should().BeGreaterThan(0);
        (await CountReferentialIdentityRowsAsync(documentId.Value)).Should().Be(1);

        var delete = await InvokeAsync(repository =>
            repository.DeleteDocumentById(CreateDeleteRequest(_schoolResourceInfo, documentUuid))
        );

        delete.Should().BeOfType<DeleteResult.DeleteSuccess>();
        (await CountDocumentsAsync(documentUuid)).Should().Be(0);
        (await CountSchoolRootRowsAsync(documentId.Value))
            .Should()
            .Be(0, "the School root row must cascade when the Document row is removed");
        (await CountSchoolAddressRowsAsync(documentId.Value))
            .Should()
            .Be(0, "SchoolAddress child rows must cascade when the Document row is removed");
        (await CountSchoolAddressPeriodRowsAsync(documentId.Value))
            .Should()
            .Be(0, "SchoolAddressPeriod child rows must cascade when the Document row is removed");
        (await CountReferentialIdentityRowsAsync(documentId.Value))
            .Should()
            .Be(0, "dms.ReferentialIdentity rows must cascade when the Document row is removed");
        _recordingLogger.Records.Should().NotContain(r => r.Message.Contains("FK constraint '"));
    }

    [Test]
    public async Task It_returns_not_exists_when_the_document_uuid_is_unknown()
    {
        var delete = await InvokeAsync(repository =>
            repository.DeleteDocumentById(
                CreateDeleteRequest(_schoolResourceInfo, new DocumentUuid(Guid.NewGuid()))
            )
        );

        delete.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        _recordingLogger.Records.Should().NotContain(r => r.Message.Contains("FK constraint '"));
    }

    [Test]
    public async Task It_returns_delete_failure_reference_with_the_resolved_referencing_resource_name_when_the_document_is_referenced_by_another_document()
    {
        // Seed a Program document, then stitch a School document with a
        // SchoolExtensionAddressSponsorReference row that FK-references the Program via
        // FK_SchoolExtensionAddressSponsorReference_Program_RefKey (ON DELETE NO ACTION).
        // Deleting the Program must be refused by the database and surface as
        // DeleteResult.DeleteFailureReference(["School"]) through the classifier +
        // constraint-resolver chain.
        //
        // This also exercises the cross-resource model walk: the violated FK lives on a grandchild
        // table of the School resource while the delete target is Program. On MSSQL the driver
        // wraps error 547 and surfaces a "REFERENCE constraint '...'" message (different phrasing
        // from INSERT/UPDATE's "FOREIGN KEY constraint '...'") — MssqlRelationalWriteExceptionClassifier's
        // ForeignKeyConstraintNameRegex must accept both forms for this assertion to hold.
        //
        // A localized / missing-constraint-name fallback assertion at integration level is not
        // feasible here: Microsoft.Data.SqlClient.SqlException is sealed with no public
        // constructor, so the test harness can't forge a localized 547. That branch is covered
        // by the RelationalDocumentStoreRepositoryTests / Given_Descriptor_Write_Handler_Delete
        // unit fixtures, which exercise UnrecognizedWriteFailure directly through the classifier
        // stub.
        var programDocumentUuid = new DocumentUuid(Guid.Parse("eeeeeeee-0000-0000-0000-000000000100"));
        var programDocumentId = await InsertDocumentAsync(programDocumentUuid.Value, "Ed-Fi", "Program");
        await InsertProgramAsync(programDocumentId, "Robotics");

        var schoolDocumentUuid = Guid.Parse("eeeeeeee-0000-0000-0000-000000000101");
        var schoolDocumentId = await InsertDocumentAsync(schoolDocumentUuid, "Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId: 900001);
        await InsertSchoolExtensionAsync(schoolDocumentId, "North");
        var addressCollectionItemId = await InsertSchoolAddressAsync(schoolDocumentId, 1, "Austin");
        await InsertSchoolExtensionAddressAsync(addressCollectionItemId, schoolDocumentId, "Zone-1");
        await InsertSchoolExtensionAddressSponsorReferenceAsync(
            addressCollectionItemId,
            schoolDocumentId,
            1,
            programDocumentId,
            "Robotics"
        );

        var delete = await InvokeAsync(repository =>
            repository.DeleteDocumentById(CreateDeleteRequest(_unrelatedResourceInfo, programDocumentUuid))
        );

        var reference = delete.Should().BeOfType<DeleteResult.DeleteFailureReference>().Subject;
        reference
            .ReferencingDocumentResourceNames.Should()
            .BeEquivalentTo(
                ["School"],
                "the FK lives on a grandchild table of the School resource, so the resolver must walk the compiled model across resources and surface the ROOT resource name — not the child table name"
            );
        (await CountDocumentsAsync(programDocumentUuid))
            .Should()
            .Be(
                1,
                "the Program row must still be present when the database refuses the DELETE due to an active reference"
            );

        // Diagnostics AC: prove the FK-violation Debug log emitted by
        // RelationalDeleteExecution.MapForeignKeyViolation carries the real driver-supplied
        // constraint name extracted from the "REFERENCE constraint '...'" DELETE phrasing, the
        // resolved referencing resource, and the original SqlException — not a wrapped or
        // swallowed exception. DeleteDocumentById emits an unrelated 'Entering' Debug record on
        // every call, so filter by message shape rather than by total count.
        var fkLog = _recordingLogger
            .Records.Should()
            .ContainSingle(r => r.Level == LogLevel.Debug && r.Message.Contains("FK constraint '"))
            .Subject;
        fkLog.Message.Should().Contain("FK_SchoolExtensionAddressSponsorReference_Program_RefKey");
        fkLog.Message.Should().Contain("referencing resource 'School'");
        fkLog.Exception.Should().BeOfType<SqlException>();
    }

    [Test]
    public async Task It_returns_not_exists_when_the_uuid_belongs_to_a_different_resource()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("eeeeeeee-0000-0000-0000-000000000002"));
        var upsert = await InvokeAsync(repository =>
            repository.UpsertDocument(CreateUpsertRequest(documentUuid))
        );
        upsert.Should().BeOfType<UpsertResult.InsertSuccess>();

        var delete = await InvokeAsync(repository =>
            repository.DeleteDocumentById(CreateDeleteRequest(_unrelatedResourceInfo, documentUuid))
        );

        delete.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        (await CountDocumentsAsync(documentUuid))
            .Should()
            .Be(1, "cross-resource DELETE must not remove the original School row");
        _recordingLogger.Records.Should().NotContain(r => r.Message.Contains("FK constraint '"));
    }

    private async Task<TResult> InvokeAsync<TResult>(
        Func<RelationalDocumentStoreRepository, Task<TResult>> action
    )
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalDeleteById",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await action(repository);
    }

    private UpsertRequest CreateUpsertRequest(DocumentUuid documentUuid)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new UpsertRequest(
            ResourceInfo: _schoolResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: identity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(_schoolResourceInfo, identity),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId("mssql-delete-setup"),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlDeleteByIdNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlDeleteByIdAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    private DeleteRequest CreateDeleteRequest(ResourceInfo resourceInfo, DocumentUuid documentUuid)
    {
        return new DeleteRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: resourceInfo,
            ResourceAuthorizationHandler: new MssqlDeleteByIdAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            TraceId: new TraceId("mssql-delete-invocation"),
            DeleteInEdOrgHierarchy: false,
            Headers: [],
            MappingSet: _mappingSet
        );
    }

    private async Task<long> CountDocumentsAsync(DocumentUuid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT_BIG(*) AS [Count]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

        return Convert.ToInt64(rows[0]["Count"]);
    }

    private async Task<long?> GetDocumentIdAsync(DocumentUuid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

        return rows.Count == 0 ? null : Convert.ToInt64(rows[0]["DocumentId"]);
    }

    private async Task<long> CountSchoolRootRowsAsync(long documentId)
    {
        return await CountByDocumentIdAsync(
            """
            SELECT COUNT_BIG(*) AS [Count]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            documentId
        );
    }

    private async Task<long> CountSchoolAddressRowsAsync(long documentId)
    {
        return await CountByDocumentIdAsync(
            """
            SELECT COUNT_BIG(*) AS [Count]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId;
            """,
            documentId
        );
    }

    private async Task<long> CountSchoolAddressPeriodRowsAsync(long documentId)
    {
        return await CountByDocumentIdAsync(
            """
            SELECT COUNT_BIG(*) AS [Count]
            FROM [edfi].[SchoolAddressPeriod]
            WHERE [School_DocumentId] = @documentId;
            """,
            documentId
        );
    }

    private async Task<long> CountReferentialIdentityRowsAsync(long documentId)
    {
        return await CountByDocumentIdAsync(
            """
            SELECT COUNT_BIG(*) AS [Count]
            FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId;
            """,
            documentId
        );
    }

    private async Task<long> CountByDocumentIdAsync(string sql, long documentId)
    {
        var rows = await _database.QueryRowsAsync(sql, new SqlParameter("@documentId", documentId));

        return Convert.ToInt64(rows[0]["Count"]);
    }

    private Task<short> GetResourceKeyIdAsync(string projectName, string resourceName) =>
        _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );

    private async Task<long> InsertDocumentAsync(Guid documentUuid, string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        // Use OUTPUT INTO (rather than bare OUTPUT or SCOPE_IDENTITY()) so this works against
        // dms.Document even though the table has an enabled trigger (SQL Server error 334
        // forbids bare OUTPUT on triggered tables). OUTPUT ... INTO @tmp is allowed.
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @ids TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @ids
            VALUES (@documentUuid, @resourceKeyId);
            SELECT [DocumentId] FROM @ids;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private Task InsertProgramAsync(long documentId, string programName) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Program] ([DocumentId], [ProgramName])
            VALUES (@documentId, @programName);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@programName", programName)
        );

    private Task InsertSchoolAsync(long documentId, int schoolId) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [SchoolId])
            VALUES (@documentId, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId)
        );

    private Task InsertSchoolExtensionAsync(long documentId, string campusCode) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtension] ([DocumentId], [CampusCode])
            VALUES (@documentId, @campusCode);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@campusCode", campusCode)
        );

    private Task<long> InsertSchoolAddressAsync(long schoolDocumentId, int ordinal, string city) =>
        // edfi.SchoolAddress.CollectionItemId is NOT an IDENTITY column — it defaults from
        // dms.CollectionItemIdSequence. So SCOPE_IDENTITY() returns NULL after this INSERT.
        // OUTPUT ... INTO @tmp captures the sequence-assigned value directly and is also
        // trigger-safe (unlike bare OUTPUT, which SQL Server 334 rejects on triggered tables).
        _database.ExecuteScalarAsync<long>(
            """
            DECLARE @ids TABLE ([CollectionItemId] bigint);
            INSERT INTO [edfi].[SchoolAddress] ([Ordinal], [School_DocumentId], [City])
            OUTPUT INSERTED.[CollectionItemId] INTO @ids
            VALUES (@ordinal, @schoolDocumentId, @city);
            SELECT [CollectionItemId] FROM @ids;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@city", city)
        );

    private Task InsertSchoolExtensionAddressAsync(
        long baseCollectionItemId,
        long schoolDocumentId,
        string zone
    ) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtensionAddress] ([BaseCollectionItemId], [School_DocumentId], [Zone])
            VALUES (@baseCollectionItemId, @schoolDocumentId, @zone);
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@zone", zone)
        );

    private Task InsertSchoolExtensionAddressSponsorReferenceAsync(
        long baseCollectionItemId,
        long schoolDocumentId,
        int ordinal,
        long programDocumentId,
        string programName
    ) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtensionAddressSponsorReference]
                ([BaseCollectionItemId], [Ordinal], [School_DocumentId], [Program_DocumentId], [Program_ProgramName])
            VALUES (@baseCollectionItemId, @ordinal, @schoolDocumentId, @programDocumentId, @programName);
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@programDocumentId", programDocumentId),
            new SqlParameter("@programName", programName)
        );

    private static ServiceProvider CreateServiceProvider(
        RecordingLogger<RelationalDocumentStoreRepository> recordingLogger
    )
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILogger<RelationalDocumentStoreRepository>>(recordingLogger);
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
