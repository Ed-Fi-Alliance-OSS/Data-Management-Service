// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlIfMatchCascadeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlIfMatchCascadeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class MssqlIfMatchCascadeReferentialIdentityTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private static readonly BaseResourceInfo StudentReferenceResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("Student"),
        false
    );

    public static readonly ResourceInfo StudentResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Student"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: true,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static readonly ResourceInfo ResourceAResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ResourceA"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton<IReadableProfileProjector, ReadableProfileProjector>();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static DocumentInfo CreateDocumentInfo(ResourceInfo resourceInfo, JsonNode requestBody) =>
        resourceInfo.ResourceName.Value switch
        {
            "Student" => CreateStudentDocumentInfo(resourceInfo, requestBody),
            "ResourceA" => CreateResourceADocumentInfo(resourceInfo, requestBody),
            _ => throw new InvalidOperationException(
                $"Unsupported resource '{resourceInfo.ResourceName.Value}' for this test fixture."
            ),
        };

    private static DocumentInfo CreateStudentDocumentInfo(ResourceInfo resourceInfo, JsonNode requestBody)
    {
        var studentUniqueId =
            requestBody["studentUniqueId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected studentUniqueId on Student request body.");

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), studentUniqueId),
        ]);

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private static DocumentInfo CreateResourceADocumentInfo(ResourceInfo resourceInfo, JsonNode requestBody)
    {
        var resourceAId =
            requestBody["resourceAId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected resourceAId on ResourceA request body.");
        var studentUniqueId =
            requestBody["studentReference"]?["studentUniqueId"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Expected studentReference.studentUniqueId on ResourceA request body."
            );

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.resourceAId"), resourceAId),
            new DocumentIdentityElement(new JsonPath("$.studentReference.studentUniqueId"), studentUniqueId),
        ]);
        var studentIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), studentUniqueId),
        ]);

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, identity),
            DocumentReferences:
            [
                new DocumentReference(
                    StudentReferenceResourceInfo,
                    studentIdentity,
                    ReferentialIdCalculator.ReferentialIdFrom(StudentReferenceResourceInfo, studentIdentity),
                    new JsonPath("$.studentReference")
                ),
            ],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_IfMatch_Cascade_Referential_Identity_Fixture
{
    private const string OriginalStudentBodyJson = """
        {
          "studentUniqueId": "10001",
          "firstName": "Casey"
        }
        """;
    private const string OriginalResourceABodyJson = """
        {
          "resourceAId": "resource-a-1",
          "studentReference": {
            "studentUniqueId": "10001"
          }
        }
        """;
    private const string UpdatedResourceABodyJson = """
        {
          "resourceAId": "resource-a-1",
          "studentReference": {
            "studentUniqueId": "10002"
          }
        }
        """;

    private static readonly DocumentUuid StudentDocumentUuid = new(
        Guid.Parse("f9f8830d-cb7f-4913-aed6-9f3dbfd784e4")
    );
    private static readonly DocumentUuid ResourceADocumentUuid = new(
        Guid.Parse("1b6fe241-7460-4db3-ab6e-f6f4ef2d7343")
    );

    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlIfMatchCascadeReferentialIdentityTestSupport.FixtureRelativePath
        );
        _mappingSet = fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = MssqlIfMatchCascadeReferentialIdentityTestSupport.CreateServiceProvider();
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
    public async Task It_returns_412_for_stale_IfMatch_after_a_student_identity_cascade_changes_resourcea_etag()
    {
        var createStudentResult = await ExecuteUpsertAsync(
            MssqlIfMatchCascadeReferentialIdentityTestSupport.StudentResourceInfo,
            OriginalStudentBodyJson,
            StudentDocumentUuid,
            "mssql-if-match-cascade-create-student"
        );
        createStudentResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var createResourceAResult = await ExecuteUpsertAsync(
            MssqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            OriginalResourceABodyJson,
            ResourceADocumentUuid,
            "mssql-if-match-cascade-create-resource-a"
        );
        createResourceAResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var getBeforeCascade = await ExecuteGetByIdAsync(
            MssqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            ResourceADocumentUuid,
            "mssql-if-match-cascade-get-before"
        );
        getBeforeCascade.Should().BeOfType<GetResult.GetSuccess>();
        var successBeforeCascade = (GetResult.GetSuccess)getBeforeCascade;
        var staleEtag = successBeforeCascade.EdfiDoc["_etag"]!.GetValue<string>();

        await UpdateStudentUniqueIdAsync("10002");

        var getAfterCascade = await ExecuteGetByIdAsync(
            MssqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            ResourceADocumentUuid,
            "mssql-if-match-cascade-get-after"
        );
        getAfterCascade.Should().BeOfType<GetResult.GetSuccess>();
        var successAfterCascade = (GetResult.GetSuccess)getAfterCascade;
        var currentEtag = successAfterCascade.EdfiDoc["_etag"]!.GetValue<string>();

        successAfterCascade.EdfiDoc["studentReference"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be("10002");
        currentEtag.Should().NotBe(staleEtag);

        var staleIfMatchResult = await ExecuteUpdateAsync(
            MssqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            UpdatedResourceABodyJson,
            ResourceADocumentUuid,
            "mssql-if-match-cascade-update-resource-a-stale",
            new Dictionary<string, string> { ["If-Match"] = staleEtag }
        );

        staleIfMatchResult.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(
        ResourceInfo resourceInfo,
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody =
            JsonNode.Parse(requestBodyJson)
            ?? throw new InvalidOperationException("Expected upsert request body to parse.");
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            new UpsertRequest(
                ResourceInfo: resourceInfo,
                DocumentInfo: MssqlIfMatchCascadeReferentialIdentityTestSupport.CreateDocumentInfo(
                    resourceInfo,
                    requestBody
                ),
                MappingSet: _mappingSet,
                EdfiDoc: requestBody,
                Headers: [],
                TraceId: new TraceId(traceId),
                DocumentUuid: documentUuid,
                DocumentSecurityElements: new([], [], [], [], []),
                UpdateCascadeHandler: new MssqlIfMatchCascadeNoOpUpdateCascadeHandler(),
                ResourceAuthorizationHandler: new MssqlIfMatchCascadeAllowAllResourceAuthorizationHandler(),
                ResourceAuthorizationPathways: []
            )
        );
    }

    private async Task<UpdateResult> ExecuteUpdateAsync(
        ResourceInfo resourceInfo,
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        Dictionary<string, string>? headers = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody =
            JsonNode.Parse(requestBodyJson)
            ?? throw new InvalidOperationException("Expected update request body to parse.");
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            new UpdateRequest(
                ResourceInfo: resourceInfo,
                DocumentInfo: MssqlIfMatchCascadeReferentialIdentityTestSupport.CreateDocumentInfo(
                    resourceInfo,
                    requestBody
                ),
                MappingSet: _mappingSet,
                EdfiDoc: requestBody,
                Headers: headers ?? [],
                TraceId: new TraceId(traceId),
                DocumentUuid: documentUuid,
                DocumentSecurityElements: new([], [], [], [], []),
                UpdateCascadeHandler: new MssqlIfMatchCascadeNoOpUpdateCascadeHandler(),
                ResourceAuthorizationHandler: new MssqlIfMatchCascadeAllowAllResourceAuthorizationHandler(),
                ResourceAuthorizationPathways: []
            )
        );
    }

    private async Task<GetResult> ExecuteGetByIdAsync(
        ResourceInfo resourceInfo,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.GetDocumentById(
            new RelationalGetRequest(
                DocumentUuid: documentUuid,
                ResourceInfo: resourceInfo,
                MappingSet: _mappingSet,
                ResourceAuthorizationHandler: new MssqlIfMatchCascadeAllowAllResourceAuthorizationHandler(),
                AuthorizationStrategyEvaluators: [],
                TraceId: new TraceId(traceId)
            )
        );
    }

    private Task UpdateStudentUniqueIdAsync(string studentUniqueId) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Student]
            SET [StudentUniqueId] = @studentUniqueId
            WHERE [DocumentId] = (
                SELECT [DocumentId]
                FROM [dms].[Document]
                WHERE [DocumentUuid] = @documentUuid
            );
            """,
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@documentUuid", StudentDocumentUuid.Value)
        );

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlIfMatchCascadeReferentialIdentity",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}
