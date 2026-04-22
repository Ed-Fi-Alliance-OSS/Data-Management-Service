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
        _serviceProvider = CreateServiceProvider();
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

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
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
