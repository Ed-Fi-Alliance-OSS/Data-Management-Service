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
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed class MssqlRelationalQueryExecutionRecorder
{
    public List<PageKeysetSpec> HydrationKeysets { get; } = [];
    public List<long> MaterializedDocumentIds { get; } = [];

    public void Reset()
    {
        HydrationKeysets.Clear();
        MaterializedDocumentIds.Clear();
    }
}

internal sealed class RecordingMssqlDocumentHydrator(
    IDmsInstanceSelection dmsInstanceSelection,
    MssqlRelationalQueryExecutionRecorder recorder
) : IDocumentHydrator
{
    private readonly IDmsInstanceSelection _dmsInstanceSelection =
        dmsInstanceSelection ?? throw new ArgumentNullException(nameof(dmsInstanceSelection));
    private readonly MssqlRelationalQueryExecutionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken ct
    )
    {
        _recorder.HydrationKeysets.Add(keyset);

        var selectedInstance = _dmsInstanceSelection.GetSelectedDmsInstance();

        await using var connection = new SqlConnection(selectedInstance.ConnectionString);
        await connection.OpenAsync(ct);

        return await HydrationExecutor.ExecuteAsync(connection, plan, keyset, SqlDialect.Mssql, null, ct);
    }
}

internal sealed class RecordingRelationalReadMaterializer(MssqlRelationalQueryExecutionRecorder recorder)
    : IRelationalReadMaterializer
{
    private readonly MssqlRelationalQueryExecutionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly IRelationalReadMaterializer _inner = CreateInnerMaterializer();

    public JsonNode Materialize(RelationalReadMaterializationRequest request)
    {
        _recorder.MaterializedDocumentIds.Add(request.DocumentMetadata.DocumentId);
        return _inner.Materialize(request);
    }

    public IReadOnlyList<MaterializedDocument> MaterializePage(
        RelationalReadPageMaterializationRequest request
    )
    {
        var materializedDocuments = _inner.MaterializePage(request);
        _recorder.MaterializedDocumentIds.AddRange(
            materializedDocuments.Select(static document => document.DocumentMetadata.DocumentId)
        );
        return materializedDocuments;
    }

    private static IRelationalReadMaterializer CreateInnerMaterializer()
    {
        var innerType =
            typeof(RelationalReadMaterializationRequest).Assembly.GetType(
                "EdFi.DataManagementService.Backend.RelationalReadMaterializer",
                throwOnError: true
            )
            ?? throw new InvalidOperationException(
                "Could not resolve internal relational read materializer type."
            );

        return Activator.CreateInstance(innerType, nonPublic: true) as IRelationalReadMaterializer
            ?? throw new InvalidOperationException(
                "Could not construct internal relational read materializer."
            );
    }
}

internal sealed class ThrowingRelationalReadTargetLookupService : IRelationalReadTargetLookupService
{
    public Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        throw new AssertionException(
            "Relational query execution should not route through get-by-id read target lookup."
        );
    }
}

internal sealed record QuerySchoolSeed(DocumentUuid DocumentUuid, int SchoolId, string NameOfInstitution);

internal sealed record PersistedQuerySchool(
    long DocumentId,
    Guid DocumentUuid,
    int SchoolId,
    string NameOfInstitution
);

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Relational_Query_With_The_Authoritative_Sample_School_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const int MaximumPageSize = 500;
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000011")),
            255901,
            "Lantern High School"
        ),
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000022")),
            255902,
            "Summit High School"
        ),
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000033")),
            255903,
            "Cedar High School"
        ),
    ];

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlRelationalQueryExecutionRecorder _recorder = null!;
    private ResourceInfo _resourceInfo = null!;
    private IReadOnlyList<PersistedQuerySchool> _persistedSchoolsInDocumentOrder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();
        _recorder = _serviceProvider.GetRequiredService<MssqlRelationalQueryExecutionRecorder>();

        var (projectSchema, resourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "School"
        );

        _resourceInfo = CreateResourceInfo(projectSchema, resourceSchema);

        foreach (var schoolSeed in _schoolSeeds)
        {
            await SeedSchoolAsync(schoolSeed);
        }

        _persistedSchoolsInDocumentOrder = await ReadPersistedSchoolsInDocumentOrderAsync();
        _persistedSchoolsInDocumentOrder.Should().HaveCount(3);
        _recorder.Reset();
    }

    [SetUp]
    public void Setup()
    {
        _recorder.Reset();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_filters_by_a_scalar_field_and_returns_matching_total_count()
    {
        var expectedSchool = _persistedSchoolsInDocumentOrder[1];

        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "nameOfInstitution",
                    "$.nameOfInstitution",
                    expectedSchool.NameOfInstitution
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "mssql-query-scalar-filter"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(expectedSchool.DocumentUuid.ToString());
        success.EdfiDocs[0]!["schoolId"]!.GetValue<long>().Should().Be(expectedSchool.SchoolId);
        success.EdfiDocs[0]!["nameOfInstitution"]!
            .GetValue<string>()
            .Should()
            .Be(expectedSchool.NameOfInstitution);

        var keyset = AssertSingleQueryHydration();
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        _recorder.MaterializedDocumentIds.Should().Equal(expectedSchool.DocumentId);
    }

    [Test]
    public async Task It_pages_in_document_id_order_and_only_materializes_the_requested_page()
    {
        var firstPageResult = await ExecuteQueryAsync(
            [],
            limit: 2,
            offset: 0,
            totalCount: true,
            traceId: "mssql-query-page-1"
        );

        var firstPageSuccess = firstPageResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        firstPageSuccess.TotalCount.Should().Be(3);
        firstPageSuccess
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _persistedSchoolsInDocumentOrder[0].DocumentUuid.ToString(),
                _persistedSchoolsInDocumentOrder[1].DocumentUuid.ToString()
            );
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        _recorder
            .MaterializedDocumentIds.Should()
            .Equal(
                _persistedSchoolsInDocumentOrder[0].DocumentId,
                _persistedSchoolsInDocumentOrder[1].DocumentId
            );

        _recorder.Reset();

        var secondPageResult = await ExecuteQueryAsync(
            [],
            limit: 2,
            offset: 2,
            totalCount: true,
            traceId: "mssql-query-page-2"
        );

        var secondPageSuccess = secondPageResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        secondPageSuccess.TotalCount.Should().Be(3);
        secondPageSuccess
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_persistedSchoolsInDocumentOrder[2].DocumentUuid.ToString());
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        _recorder.MaterializedDocumentIds.Should().Equal(_persistedSchoolsInDocumentOrder[2].DocumentId);
    }

    [Test]
    public async Task It_filters_by_id_using_the_special_case_document_uuid_query_path()
    {
        var expectedSchool = _persistedSchoolsInDocumentOrder[2];

        var result = await ExecuteQueryAsync(
            [CreateQueryElement("id", "$.id", expectedSchool.DocumentUuid.ToString())],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "mssql-query-id-filter"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(expectedSchool.DocumentUuid.ToString());
        success.EdfiDocs[0]!["nameOfInstitution"]!
            .GetValue<string>()
            .Should()
            .Be(expectedSchool.NameOfInstitution);

        AssertSingleQueryHydration();
        _recorder.MaterializedDocumentIds.Should().Equal(expectedSchool.DocumentId);
    }

    [Test]
    public async Task It_enforces_case_sensitive_string_filtering_for_sql_server()
    {
        var expectedSchool = _persistedSchoolsInDocumentOrder[0];

        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "nameOfInstitution",
                    "$.nameOfInstitution",
                    expectedSchool.NameOfInstitution.ToLowerInvariant()
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "mssql-query-case-sensitive-filter"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(0);
        success.EdfiDocs.Should().BeEmpty();
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        _recorder.MaterializedDocumentIds.Should().BeEmpty();
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddSingleton<MssqlRelationalQueryExecutionRecorder>();
        services.AddMssqlReferenceResolver();
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, RecordingMssqlDocumentHydrator>());
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

    private static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        var effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
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
                $"Could not find resource '{resourceName}' in project '{projectEndpointName}'."
            );

        return (projectSchema, new ResourceSchema(resourceSchemaNode));
    }

    private static ResourceInfo CreateResourceInfo(
        ProjectSchema projectSchema,
        ResourceSchema resourceSchema
    ) =>
        new(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

    private async Task<QueryResult> ExecuteQueryAsync(
        QueryElement[] queryElements,
        int? limit,
        int? offset,
        bool totalCount,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalQueryRequest(
            ResourceInfo: _resourceInfo,
            MappingSet: _mappingSet,
            QueryElements: queryElements,
            AuthorizationSecurableInfo: _resourceInfo.AuthorizationSecurableInfo,
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId(traceId)
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    private async Task<IReadOnlyList<PersistedQuerySchool>> ReadPersistedSchoolsInDocumentOrderAsync()
    {
        var resourceKeyId = _mappingSet.ResourceKeyIdByResource[SchoolResource];
        var physicalSchema = _mappingSet.ReadPlansByResource[SchoolResource].Model.PhysicalSchema.Value;
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                doc.[DocumentId],
                doc.[DocumentUuid],
                school.[SchoolId],
                school.[NameOfInstitution]
            FROM [dms].[Document] doc
            INNER JOIN [{physicalSchema}].[School] school
                ON school.[DocumentId] = doc.[DocumentId]
            WHERE doc.[ResourceKeyId] = @resourceKeyId
            ORDER BY doc.[DocumentId];
            """,
            new SqlParameter("@resourceKeyId", resourceKeyId)
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

    private async Task SeedSchoolAsync(QuerySchoolSeed schoolSeed)
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var documentId = await InsertDocumentAsync(schoolSeed.DocumentUuid.Value, resourceKeyId);
        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () => await InsertSchoolAsync(documentId, schoolSeed.SchoolId, schoolSeed.NameOfInstitution)
        );
        await InsertEducationOrganizationIdentityAsync(documentId, schoolSeed.SchoolId, "Ed-Fi:School");
        await UpsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", schoolSeed.SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            documentId,
            resourceKeyId
        );
        await UpsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", schoolSeed.SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            documentId,
            educationOrganizationResourceKeyId
        );
    }

    private PageKeysetSpec.Query AssertSingleQueryHydration()
    {
        _recorder.HydrationKeysets.Should().ContainSingle();
        _recorder.HydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Query>();
        return (PageKeysetSpec.Query)_recorder.HydrationKeysets[0];
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalQueryExecution",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task InsertEducationOrganizationIdentityAsync(
        long documentId,
        int educationOrganizationId,
        string discriminator
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[EducationOrganizationIdentity] (
                [DocumentId],
                [EducationOrganizationId],
                [Discriminator]
            )
            VALUES (@documentId, @educationOrganizationId, @discriminator);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@discriminator", discriminator)
        );
    }

    private async Task UpsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId
              AND [ResourceKeyId] = @resourceKeyId;

            DELETE FROM [dms].[ReferentialIdentity]
            WHERE [ReferentialId] = @referentialId;

            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", referentialId.Value),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task ExecuteWithTriggersTemporarilyDisabledAsync(
        string schema,
        string table,
        Func<Task> action
    )
    {
        await _database.ExecuteNonQueryAsync($"""DISABLE TRIGGER ALL ON [{schema}].[{table}];""");

        try
        {
            await action();
        }
        finally
        {
            await _database.ExecuteNonQueryAsync($"""ENABLE TRIGGER ALL ON [{schema}].[{table}];""");
        }
    }

    private static ReferentialId CreateReferentialId(
        (string ProjectName, string ResourceName, bool IsDescriptor) targetResource,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                targetResource.IsDescriptor
            ),
            new DocumentIdentity([
                .. identityElements.Select(identityElement => new DocumentIdentityElement(
                    new JsonPath(identityElement.IdentityJsonPath),
                    identityElement.IdentityValue
                )),
            ])
        );
    }

    private static QueryElement CreateQueryElement(string queryFieldName, string path, string value)
    {
        return new QueryElement(queryFieldName, [new JsonPath(path)], value, "string");
    }

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
