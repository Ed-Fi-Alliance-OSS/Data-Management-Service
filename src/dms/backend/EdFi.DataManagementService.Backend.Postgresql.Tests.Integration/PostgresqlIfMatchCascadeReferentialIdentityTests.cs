// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class PostgresqlIfMatchCascadeHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostgresqlIfMatchCascadeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostgresqlIfMatchCascadeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class PostgresqlIfMatchCascadeReferentialIdentityTestSupport
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

        services.AddSingleton<IHostApplicationLifetime, PostgresqlIfMatchCascadeHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton<IReadableProfileProjector, ReadableProfileProjector>();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

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
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_IfMatch_Cascade_Referential_Identity_Fixture
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
        Guid.Parse("8af6e58d-df98-49f5-a65d-7cb0f2391b11")
    );
    private static readonly DocumentUuid ResourceADocumentUuid = new(
        Guid.Parse("6bf3e31b-a799-4522-b079-68fda3a4d822")
    );

    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlIfMatchCascadeReferentialIdentityTestSupport.FixtureRelativePath
        );
        _mappingSet = fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = PostgresqlIfMatchCascadeReferentialIdentityTestSupport.CreateServiceProvider();
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
            PostgresqlIfMatchCascadeReferentialIdentityTestSupport.StudentResourceInfo,
            OriginalStudentBodyJson,
            StudentDocumentUuid,
            "pg-if-match-cascade-create-student"
        );
        createStudentResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var createResourceAResult = await ExecuteUpsertAsync(
            PostgresqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            OriginalResourceABodyJson,
            ResourceADocumentUuid,
            "pg-if-match-cascade-create-resource-a"
        );
        createResourceAResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var getBeforeCascade = await ExecuteGetByIdAsync(
            PostgresqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            ResourceADocumentUuid,
            "pg-if-match-cascade-get-before"
        );
        getBeforeCascade.Should().BeOfType<GetResult.GetSuccess>();
        var successBeforeCascade = (GetResult.GetSuccess)getBeforeCascade;
        var staleEtag = successBeforeCascade.EdfiDoc["_etag"]!.GetValue<string>();

        await UpdateStudentUniqueIdAsync("10002");

        var getAfterCascade = await ExecuteGetByIdAsync(
            PostgresqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            ResourceADocumentUuid,
            "pg-if-match-cascade-get-after"
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
            PostgresqlIfMatchCascadeReferentialIdentityTestSupport.ResourceAResourceInfo,
            UpdatedResourceABodyJson,
            ResourceADocumentUuid,
            "pg-if-match-cascade-update-resource-a-stale",
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
                DocumentInfo: PostgresqlIfMatchCascadeReferentialIdentityTestSupport.CreateDocumentInfo(
                    resourceInfo,
                    requestBody
                ),
                MappingSet: _mappingSet,
                EdfiDoc: requestBody,
                Headers: [],
                TraceId: new TraceId(traceId),
                DocumentUuid: documentUuid,
                DocumentSecurityElements: new([], [], [], [], []),
                UpdateCascadeHandler: new PostgresqlIfMatchCascadeNoOpUpdateCascadeHandler(),
                ResourceAuthorizationHandler: new PostgresqlIfMatchCascadeAllowAllResourceAuthorizationHandler(),
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
                DocumentInfo: PostgresqlIfMatchCascadeReferentialIdentityTestSupport.CreateDocumentInfo(
                    resourceInfo,
                    requestBody
                ),
                MappingSet: _mappingSet,
                EdfiDoc: requestBody,
                Headers: headers ?? [],
                TraceId: new TraceId(traceId),
                DocumentUuid: documentUuid,
                DocumentSecurityElements: new([], [], [], [], []),
                UpdateCascadeHandler: new PostgresqlIfMatchCascadeNoOpUpdateCascadeHandler(),
                ResourceAuthorizationHandler: new PostgresqlIfMatchCascadeAllowAllResourceAuthorizationHandler(),
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
                ResourceAuthorizationHandler: new PostgresqlIfMatchCascadeAllowAllResourceAuthorizationHandler(),
                AuthorizationStrategyEvaluators: [],
                TraceId: new TraceId(traceId)
            )
        );
    }

    private Task UpdateStudentUniqueIdAsync(string studentUniqueId) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "StudentUniqueId" = @studentUniqueId
            WHERE "DocumentId" = (
                SELECT "DocumentId"
                FROM "dms"."Document"
                WHERE "DocumentUuid" = @documentUuid
            );
            """,
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("documentUuid", StudentDocumentUuid.Value)
        );

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlIfMatchCascadeReferentialIdentity",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}
