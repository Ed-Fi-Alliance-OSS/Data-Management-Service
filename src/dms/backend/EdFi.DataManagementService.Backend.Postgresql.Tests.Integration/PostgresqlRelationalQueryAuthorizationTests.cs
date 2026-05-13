// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record ClassPeriodSeed(DocumentUuid DocumentUuid, int SchoolId, string ClassPeriodName);

internal sealed record AuthorizationAndSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationAndId,
    string Name,
    int PrimarySchoolId,
    int SecondarySchoolId
);

internal sealed record AuthorizationRootChildSeed(
    DocumentUuid DocumentUuid,
    int AuthorizationRootChildId,
    string Name,
    int SchoolId,
    IReadOnlyList<ClassPeriodReferenceSeed> ClassPeriods
);

internal sealed record AuthorizationChildOnlySeed(
    DocumentUuid DocumentUuid,
    int AuthorizationChildOnlyId,
    string Name,
    IReadOnlyList<ClassPeriodReferenceSeed> ClassPeriods
);

internal sealed record ClassPeriodReferenceSeed(string ClassPeriodName, int SchoolId);

internal sealed class PostgresqlRelationalQueryAuthorizationTestContext : IAsyncDisposable
{
    private const int MaximumPageSize = 500;
    private readonly Dictionary<
        (string ProjectEndpointName, string ResourceName),
        ResourceHandle
    > _resourceCache = [];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private ServiceProvider _serviceProvider = null!;
    private PostgresqlRelationalQueryExecutionRecorder _recorder = null!;

    public MappingSet MappingSet => _fixture.MappingSet;

    public PostgresqlGeneratedDdlTestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync(string fixtureRelativePath, bool strict)
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            fixtureRelativePath,
            strict
        );
        Database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();
        _recorder = _serviceProvider.GetRequiredService<PostgresqlRelationalQueryExecutionRecorder>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }

    public void ResetRecorder() => _recorder.Reset();

    public PageKeysetSpec.Query AssertSingleQueryHydration()
    {
        _recorder.HydrationKeysets.Should().ContainSingle();
        _recorder.HydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Query>();
        return (PageKeysetSpec.Query)_recorder.HydrationKeysets[0];
    }

    public void AssertNoHydration()
    {
        _recorder.HydrationKeysets.Should().BeEmpty();
        _recorder.PageMaterializationCallCount.Should().Be(0);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
    }

    public async Task SeedSchoolDescriptorDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("40444444-4444-4444-4444-444444444444"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("60666666-6666-6666-6666-666666666666"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
    }

    public async Task<UpsertResult> CreateSchoolAsync(QuerySchoolSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "School",
            PostgresqlRelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(seed),
            seed.DocumentUuid,
            $"seed-school-{seed.SchoolId}"
        );
    }

    public async Task<UpsertResult> CreateClassPeriodAsync(ClassPeriodSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "ClassPeriod",
            PostgresqlRelationalQueryAuthorizationRequestBodies.CreateClassPeriodRequestBody(seed),
            seed.DocumentUuid,
            $"seed-class-period-{seed.SchoolId}-{seed.ClassPeriodName}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationAndAsync(AuthorizationAndSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationAndResource",
            PostgresqlRelationalQueryAuthorizationRequestBodies.CreateAuthorizationAndRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-and-{seed.AuthorizationAndId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationRootChildAsync(AuthorizationRootChildSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationRootChildResource",
            PostgresqlRelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-root-child-{seed.AuthorizationRootChildId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationChildOnlyAsync(AuthorizationChildOnlySeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationChildOnlyResource",
            PostgresqlRelationalQueryAuthorizationRequestBodies.CreateAuthorizationChildOnlyRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-child-only-{seed.AuthorizationChildOnlyId}"
        );
    }

    public async Task InsertAuthEdgeAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" (
                "SourceEducationOrganizationId",
                "TargetEducationOrganizationId"
            )
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new NpgsqlParameter("sourceEducationOrganizationId", sourceEducationOrganizationId),
            new NpgsqlParameter("targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    public async Task<QueryResult> QueryAsync(
        string projectEndpointName,
        string resourceName,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        int? limit = null,
        int? offset = null,
        bool totalCount = true
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalQueryRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext(claimEducationOrganizationIds),
            MappingSet: MappingSet,
            QueryElements: [],
            AuthorizationSecurableInfo: resourceHandle.ResourceInfo.AuthorizationSecurableInfo,
            AuthorizationStrategyEvaluators:
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
            PaginationParameters: new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId($"{resourceName}-authorization-query")
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    public async Task<IReadOnlyList<PersistedQuerySchool>> ReadPersistedSchoolsInDocumentOrderAsync()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKeyId = MappingSet.ResourceKeyIdByResource[schoolResource];
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var rows = await Database.QueryRowsAsync(
            $"""
            SELECT
                doc."DocumentId",
                doc."DocumentUuid",
                school."SchoolId",
                school."NameOfInstitution"
            FROM "dms"."Document" doc
            INNER JOIN "{physicalSchema}"."School" school
                ON school."DocumentId" = doc."DocumentId"
            WHERE doc."ResourceKeyId" = @resourceKeyId
            ORDER BY doc."DocumentId";
            """,
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return
        [
            .. rows.Select(row => new PersistedQuerySchool(
                DocumentId: GetRequiredInt64(row, "DocumentId"),
                DocumentUuid: GetRequiredGuid(row, "DocumentUuid"),
                SchoolId: GetRequiredInt32(row, "SchoolId"),
                NameOfInstitution: GetRequiredString(row, "NameOfInstitution")
            )),
        ];
    }

    private async Task<UpsertResult> UpsertAsync(
        string projectEndpointName,
        string resourceName,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new UpsertRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                resourceHandle.ResourceInfo,
                resourceHandle.ResourceSchema,
                MappingSet
            ),
            MappingSet: MappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlRelationalQueryNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlRelationalQueryAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private ResourceHandle GetResourceHandle(string projectEndpointName, string resourceName)
    {
        var key = (projectEndpointName, resourceName);

        if (_resourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var effectiveProjectSchema = _fixture.EffectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(
                project.ProjectEndpointName,
                projectEndpointName,
                StringComparison.OrdinalIgnoreCase
            )
        );
        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project endpoint '{projectEndpointName}'."
            );

        var resourceSchema = new ResourceSchema(resourceSchemaNode);
        var resourceInfo = new ResourceInfo(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

        var resourceHandle = new ResourceHandle(projectSchema, resourceSchema, resourceInfo);
        _resourceCache[key] = resourceHandle;
        return resourceHandle;
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, PostgresqlRelationalQueryHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddSingleton<PostgresqlRelationalQueryExecutionRecorder>();
        services.AddPostgresqlReferenceResolver();
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, RecordingPostgresqlDocumentHydrator>());
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalReadMaterializer, RecordingRelationalReadMaterializer>()
        );
        services.Replace(
            ServiceDescriptor.Scoped<
                IRelationalReadTargetLookupService,
                ThrowingRelationalReadTargetLookupService
            >()
        );

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalQueryAuthorization",
                    ConnectionString: Database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task SeedDescriptorAsync(
        Guid documentUuid,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);
        var documentId = await InsertDescriptorAsync(
            documentUuid,
            resourceKeyId,
            discriminator,
            uri,
            @namespace,
            codeValue,
            shortDescription
        );

        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", resourceName, uri),
            documentId,
            resourceKeyId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await Database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(new ProjectName(projectName), new ResourceName(resourceName), true),
            new DocumentIdentity([
                new DocumentIdentityElement(
                    DocumentIdentity.DescriptorIdentityJsonPath,
                    descriptorUri.ToLowerInvariant()
                ),
            ])
        );
    }

    private sealed record ResourceHandle(
        ProjectSchema ProjectSchema,
        ResourceSchema ResourceSchema,
        ResourceInfo ResourceInfo
    );

    private static long GetRequiredInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static int GetRequiredInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static Guid GetRequiredGuid(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) as string
            ?? throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a string value."
            );
    }

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException($"Expected row to contain non-null column '{columnName}'.");
        }

        return value;
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_The_Authoritative_Ds52_School_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const long ClaimEducationOrganizationId = 900;
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly IReadOnlyList<string> _invertedStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly IReadOnlyList<string> _normalAndInvertedStrategies =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000001")), 100, "Alpha High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000002")), 200, "Beta High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000003")), 300, "Gamma High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000004")), 400, "Delta High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000005")), 500, "Epsilon High"),
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;
    private IReadOnlyList<PersistedQuerySchool> _persistedSchoolsInDocumentOrder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            var createResult = await _context.CreateSchoolAsync(schoolSeed);
            PostgresqlRelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 400);
        await _context.InsertAuthEdgeAsync(400, ClaimEducationOrganizationId);

        _persistedSchoolsInDocumentOrder = await _context.ReadPersistedSchoolsInDocumentOrderAsync();
        _persistedSchoolsInDocumentOrder
            .Select(static school => school.SchoolId)
            .Should()
            .Equal(100, 200, 300, 400, 500);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_filters_normal_relationship_authorization_for_the_derived_school_resource()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(3);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _schoolSeeds[0].DocumentUuid.Value.ToString(),
                _schoolSeeds[1].DocumentUuid.Value.ToString(),
                _schoolSeeds[3].DocumentUuid.Value.ToString()
            );

        var keyset = _context.AssertSingleQueryHydration();
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("= ANY(@ClaimEducationOrganizationIds)")
            .And.Contain("\"TargetEducationOrganizationId\"")
            .And.Contain("\"SchoolId\"");
        keyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(ClaimEducationOrganizationId);
    }

    [Test]
    public async Task It_filters_inverted_relationship_authorization_bottom_to_top()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _invertedStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _schoolSeeds[2].DocumentUuid.Value.ToString(),
                _schoolSeeds[3].DocumentUuid.Value.ToString()
            );

        _context
            .AssertSingleQueryHydration()
            .Plan.PageDocumentIdSql.Should()
            .Contain("\"SourceEducationOrganizationId\"")
            .And.Contain("\"TargetEducationOrganizationId\"");
    }

    [Test]
    public async Task It_ors_normal_and_inverted_relationship_authorization_without_duplicates()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalAndInvertedStrategies
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(4);
        success.EdfiDocs.Should().HaveCount(4);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _schoolSeeds[0].DocumentUuid.Value.ToString(),
                _schoolSeeds[1].DocumentUuid.Value.ToString(),
                _schoolSeeds[2].DocumentUuid.Value.ToString(),
                _schoolSeeds[3].DocumentUuid.Value.ToString()
            );
    }

    [Test]
    public async Task It_pages_and_counts_after_relationship_authorization_filtering()
    {
        var authorizedDocumentIds = _persistedSchoolsInDocumentOrder
            .Where(static school => school.SchoolId is 100 or 200 or 300 or 400)
            .Skip(1)
            .Take(2)
            .Select(static school => school.DocumentUuid.ToString())
            .ToArray();

        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalAndInvertedStrategies,
            limit: 2,
            offset: 1,
            totalCount: true
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(4);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(authorizedDocumentIds);
    }

    [Test]
    public async Task It_returns_an_empty_page_and_zero_total_count_when_claim_edorgs_are_empty()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [],
            _normalAndInvertedStrategies,
            totalCount: true
        );

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        _context.AssertNoHydration();
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_A_Synthetic_EdOrg_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/synthetic/authorization-query";
    private const long ClaimEducationOrganizationId = 900;
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000003")), 300, "West School"),
    ];
    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000002")), 200, "P2"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000003")), 300, "P3"),
    ];
    private static readonly AuthorizationAndSeed[] _authorizationAndSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000001")),
            1,
            "requires-both",
            100,
            200
        ),
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000002")),
            2,
            "missing-secondary-auth",
            100,
            300
        ),
    ];
    private static readonly AuthorizationRootChildSeed[] _authorizationRootChildSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000001")),
            1,
            "authorized-by-root",
            100,
            [new ClassPeriodReferenceSeed("P3", 300)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000002")),
            2,
            "child-would-match-but-root-does-not",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000003")),
            3,
            "authorized-with-empty-child-collection",
            100,
            []
        ),
    ];
    private static readonly AuthorizationChildOnlySeed _authorizationChildOnlySeed = new(
        new DocumentUuid(Guid.Parse("66666666-0000-0000-0000-000000000001")),
        1,
        "child-only",
        [new ClassPeriodReferenceSeed("P1", 100)]
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: false);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            var createResult = await _context.CreateSchoolAsync(schoolSeed);
            PostgresqlRelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            var createResult = await _context.CreateClassPeriodAsync(classPeriodSeed);
            PostgresqlRelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var authorizationAndSeed in _authorizationAndSeeds)
        {
            var createResult = await _context.CreateAuthorizationAndAsync(authorizationAndSeed);
            PostgresqlRelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var authorizationRootChildSeed in _authorizationRootChildSeeds)
        {
            var createResult = await _context.CreateAuthorizationRootChildAsync(authorizationRootChildSeed);
            PostgresqlRelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        PostgresqlRelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationChildOnlyAsync(_authorizationChildOnlySeed)
        );

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_ands_multiple_root_base_edorg_subjects_within_one_strategy()
    {
        var result = await _context.QueryAsync(
            "authz",
            "AuthorizationAndResource",
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_authorizationAndSeeds[0].DocumentUuid.Value.ToString());

        _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql.Should().Contain(" AND ");
    }

    [Test]
    public async Task It_authorizes_root_plus_child_resources_from_the_root_subject_only_including_empty_children()
    {
        var result = await _context.QueryAsync(
            "authz",
            "AuthorizationRootChildResource",
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _authorizationRootChildSeeds[0].DocumentUuid.Value.ToString(),
                _authorizationRootChildSeeds[2].DocumentUuid.Value.ToString()
            );
    }

    [Test]
    public async Task It_returns_security_configuration_failure_for_child_only_resources()
    {
        var result = await _context.QueryAsync(
            "authz",
            "AuthorizationChildOnlyResource",
            [ClaimEducationOrganizationId],
            _normalStrategy,
            totalCount: false
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;

        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("$.classPeriods[*].classPeriodReference.schoolId");
        failure.Errors[0].Should().Contain("SchoolId");
    }
}

internal static class PostgresqlRelationalQueryAuthorizationRequestBodies
{
    public static JsonNode CreateSchoolRequestBody(QuerySchoolSeed schoolSeed)
    {
        return new JsonObject
        {
            ["schoolId"] = (long)schoolSeed.SchoolId,
            ["nameOfInstitution"] = schoolSeed.NameOfInstitution,
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject
                {
                    ["gradeLevelDescriptor"] = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
                }
            ),
        };
    }

    public static JsonNode CreateClassPeriodRequestBody(ClassPeriodSeed seed)
    {
        return new JsonObject
        {
            ["classPeriodName"] = seed.ClassPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SchoolId },
        };
    }

    public static JsonNode CreateAuthorizationAndRequestBody(AuthorizationAndSeed seed)
    {
        return new JsonObject
        {
            ["authorizationAndId"] = seed.AuthorizationAndId,
            ["name"] = seed.Name,
            ["primarySchoolReference"] = new JsonObject { ["schoolId"] = (long)seed.PrimarySchoolId },
            ["secondarySchoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SecondarySchoolId },
        };
    }

    public static JsonNode CreateAuthorizationRootChildRequestBody(AuthorizationRootChildSeed seed)
    {
        JsonArray classPeriods = [];

        foreach (var classPeriod in seed.ClassPeriods)
        {
            classPeriods.Add(
                new JsonObject
                {
                    ["classPeriodReference"] = new JsonObject
                    {
                        ["classPeriodName"] = classPeriod.ClassPeriodName,
                        ["schoolId"] = (long)classPeriod.SchoolId,
                    },
                }
            );
        }

        return new JsonObject
        {
            ["authorizationRootChildId"] = seed.AuthorizationRootChildId,
            ["name"] = seed.Name,
            ["schoolReference"] = new JsonObject { ["schoolId"] = (long)seed.SchoolId },
            ["classPeriods"] = classPeriods,
        };
    }

    public static JsonNode CreateAuthorizationChildOnlyRequestBody(AuthorizationChildOnlySeed seed)
    {
        JsonArray classPeriods = [];

        foreach (var classPeriod in seed.ClassPeriods)
        {
            classPeriods.Add(
                new JsonObject
                {
                    ["classPeriodReference"] = new JsonObject
                    {
                        ["classPeriodName"] = classPeriod.ClassPeriodName,
                        ["schoolId"] = (long)classPeriod.SchoolId,
                    },
                }
            );
        }

        return new JsonObject
        {
            ["authorizationChildOnlyId"] = seed.AuthorizationChildOnlyId,
            ["name"] = seed.Name,
            ["classPeriods"] = classPeriods,
        };
    }
}

internal static class PostgresqlRelationalQueryAuthorizationAssertions
{
    public static void AssertInsertSuccess(UpsertResult result)
    {
        if (result is UpsertResult.InsertSuccess)
        {
            return;
        }

        if (result is UpsertResult.UpsertFailureValidation validationFailure)
        {
            Assert.Fail(
                "Expected insert success but received validation failures: "
                    + string.Join(
                        "; ",
                        validationFailure.ValidationFailures.Select(static failure =>
                            $"{failure.Path.Value}: {failure.Message}"
                        )
                    )
            );
        }

        Assert.Fail($"Expected insert success but received result type '{result.GetType().Name}'.");
    }
}
