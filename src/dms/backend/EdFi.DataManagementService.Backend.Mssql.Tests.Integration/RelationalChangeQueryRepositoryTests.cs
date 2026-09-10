// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Verifies RelationalChangeQueryRepository.GetNewestChangeVersion() reads [dms].[GetMaxChangeVersion]()
/// through the real SQL Server command executor and reader, tracking dms.ChangeVersionSequence.
/// Sequence helpers are shared with GetMaxChangeVersionTestBase.
/// </summary>
public abstract class RelationalChangeQueryRepositoryTestBase
{
    protected long Result { get; set; }

    protected static IChangeQueryRepository CreateRepository()
    {
        var commandExecutor = new MssqlRelationalCommandExecutor(
            static async ct =>
            {
                var connection = new SqlConnection(Uuidv5ParityTestBase.ConnectionString);
                await connection.OpenAsync(ct);
                return connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );

        return new RelationalChangeQueryRepository(
            commandExecutor,
            new MssqlRelationalParameterConfigurator()
        );
    }
}

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard3)]
public class Given_Fresh_ChangeVersionSequence_Read_Through_Repository
    : RelationalChangeQueryRepositoryTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await GetMaxChangeVersionTestBase.ResetSequenceToStart();
        Result = await CreateRepository().GetNewestChangeVersion();
    }

    [Test]
    public void It_should_return_start_value_one()
    {
        Result.Should().Be(1L);
    }
}

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard3)]
public class Given_ChangeVersionSequence_Advanced_Three_Times_Read_Through_Repository
    : RelationalChangeQueryRepositoryTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await GetMaxChangeVersionTestBase.ResetSequenceToStart();
        await GetMaxChangeVersionTestBase.AdvanceSequence(3);
        Result = await CreateRepository().GetNewestChangeVersion();
    }

    [Test]
    public void It_should_return_the_last_allocated_value()
    {
        Result.Should().Be(3L);
    }
}

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard3)]
public class Given_Repository_And_Raw_Function_Call : RelationalChangeQueryRepositoryTestBase
{
    private long _repositoryResult;
    private long _rawResult;

    [SetUp]
    public async Task Setup()
    {
        await GetMaxChangeVersionTestBase.ResetSequenceToStart();
        await GetMaxChangeVersionTestBase.AdvanceSequence(5);
        _repositoryResult = await CreateRepository().GetNewestChangeVersion();
        _rawResult = await GetMaxChangeVersionTestBase.CallFunction();
    }

    [Test]
    public void It_should_match_the_direct_function_call()
    {
        _repositoryResult.Should().Be(_rawResult);
        _repositoryResult.Should().Be(5L);
    }
}

file sealed record TestTrackedChangeQueryRequest(
    ResourceInfo ResourceInfo,
    ChangeQueryEndpointOperation Operation,
    PaginationParameters PaginationParameters,
    ChangeVersionRange ChangeVersionRange,
    TraceId TraceId,
    RelationalAuthorizationContext AuthorizationContext,
    IReadOnlyList<AuthorizationStrategyEvaluator> AuthorizationStrategyEvaluators,
    MappingSet MappingSet,
    ConcreteResourceModel ResourceModel,
    TrackedChangeTableInfo TrackedChangeTable
) : IRelationalTrackedChangeQueryRequest;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Generated_Ddl_RelationalChangeQueryRepository
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;
    private const int SchoolId = 255901;
    private const string WeekIdentifierA = "Week-ChangeQuery-A";
    private const string WeekIdentifierB = "Week-ChangeQuery-B";
    private const string WeekIdentifierC = "Week-ChangeQuery-C";
    private const string ProgramName = "Repository tracked program";
    private const string ProgramTypeDescriptorNamespace = "uri://ed-fi.org/ProgramTypeDescriptor";
    private const string ProgramTypeDescriptorCodeValue = "Repository tracked delete";
    private const string ProgramTypeDescriptorUri =
        $"{ProgramTypeDescriptorNamespace}#{ProgramTypeDescriptorCodeValue}";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName AcademicWeekResource = new("Ed-Fi", "AcademicWeek");
    private static readonly QualifiedResourceName ProgramResource = new("Ed-Fi", "Program");
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "ProgramTypeDescriptor");
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("aaaaaaaa-2000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid AcademicWeekDocumentUuid = new(
        Guid.Parse("bbbbbbbb-2000-0000-0000-000000000002")
    );
    private static readonly DocumentUuid ProgramDocumentUuid = new(
        Guid.Parse("cccccccc-2000-0000-0000-000000000003")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _schoolResourceInfo = null!;
    private ResourceSchema _schoolResourceSchema = null!;
    private ResourceInfo _academicWeekResourceInfo = null!;
    private ResourceSchema _academicWeekResourceSchema = null!;
    private ResourceInfo _programResourceInfo = null!;
    private ResourceInfo _descriptorResourceInfo = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
        _serviceProvider = CreateServiceProvider();

        (ProjectSchema schoolProjectSchema, ResourceSchema schoolSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "School"
        );
        _schoolResourceInfo = CreateResourceInfo(schoolProjectSchema, schoolSchema);
        _schoolResourceSchema = schoolSchema;

        (ProjectSchema academicWeekProjectSchema, ResourceSchema academicWeekSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "AcademicWeek"
        );
        _academicWeekResourceInfo = CreateResourceInfo(academicWeekProjectSchema, academicWeekSchema);
        _academicWeekResourceSchema = academicWeekSchema;

        (ProjectSchema programProjectSchema, ResourceSchema programSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "Program"
        );
        _programResourceInfo = CreateResourceInfo(programProjectSchema, programSchema);

        (ProjectSchema descriptorProjectSchema, ResourceSchema descriptorSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "ProgramTypeDescriptor"
        );
        _descriptorResourceInfo = CreateResourceInfo(descriptorProjectSchema, descriptorSchema);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        await SeedReferenceDataAsync();

        UpsertResult schoolResult = await UpsertSchoolAsync();
        schoolResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
        }
    }

    [Test]
    public async Task It_returns_deleted_academic_week_tombstones()
    {
        UpsertResult academicWeekResult = await UpsertAcademicWeekAsync(WeekIdentifierA);
        academicWeekResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        await DeleteAcademicWeekRootAsync(await GetAcademicWeekDocumentIdAsync());

        TrackedChangeQueryResult result = await QueryAcademicWeekTrackedChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false
        );

        result.Items.Should().ContainSingle();
        JsonObject item = result.Items[0]!.AsObject();
        item["id"]!.GetValue<string>().Should().Be(AcademicWeekDocumentUuid.Value.ToString("D"));

        JsonObject keyValues = item["keyValues"]!.AsObject();
        keyValues["weekIdentifier"]!.GetValue<string>().Should().Be(WeekIdentifierA);
        keyValues["schoolId"]!.GetValue<long>().Should().Be(SchoolId);
    }

    [Test]
    public async Task It_suppresses_deleted_academic_weeks_that_have_been_recreated()
    {
        UpsertResult academicWeekResult = await UpsertAcademicWeekAsync(WeekIdentifierA);
        academicWeekResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        long academicWeekDocumentId = await GetAcademicWeekDocumentIdAsync();

        await DeleteAcademicWeekRootAsync(academicWeekDocumentId);
        await InsertAcademicWeekRootAsync(academicWeekDocumentId, WeekIdentifierA);

        TrackedChangeQueryResult result = await QueryAcademicWeekTrackedChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: true
        );

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    [Test]
    public async Task It_collapses_multiple_academic_week_key_changes_to_one_result()
    {
        UpsertResult academicWeekResult = await UpsertAcademicWeekAsync(WeekIdentifierA);
        academicWeekResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        long academicWeekDocumentId = await GetAcademicWeekDocumentIdAsync();

        await UpdateAcademicWeekIdentifierAsync(academicWeekDocumentId, WeekIdentifierB);
        await UpdateAcademicWeekIdentifierAsync(academicWeekDocumentId, WeekIdentifierC);

        (long firstChangeVersion, long latestChangeVersion) = await GetAcademicWeekKeyChangeVersionsAsync();
        TrackedChangeQueryResult result = await QueryAcademicWeekTrackedChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: true
        );

        ChangeQueryParityScenarios.AssertAcademicWeekKeyChangesCollapsedToLatestStoredChangeVersion(
            result,
            AcademicWeekDocumentUuid.Value,
            firstChangeVersion,
            latestChangeVersion,
            SchoolId,
            WeekIdentifierA,
            WeekIdentifierC
        );
    }

    [Test]
    public async Task It_returns_empty_descriptor_key_changes()
    {
        TrackedChangeQueryResult result = await QueryDescriptorTrackedChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: true
        );

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    [Test]
    public async Task It_returns_deleted_descriptor_key_values()
    {
        long descriptorDocumentId = await SeedDescriptorAsync(
            Guid.Parse("c0000003-2000-0000-0000-000000000003"),
            "ProgramTypeDescriptor",
            "ProgramTypeDescriptor",
            ProgramTypeDescriptorUri,
            ProgramTypeDescriptorNamespace,
            ProgramTypeDescriptorCodeValue,
            ProgramTypeDescriptorCodeValue
        );

        await DeleteDescriptorAsync(descriptorDocumentId);

        TrackedChangeQueryResult result = await QueryDescriptorTrackedChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: true,
            changeVersionRange: new ChangeVersionRange(0, long.MaxValue)
        );

        result.TotalCount.Should().Be(1L);
        result.Items.Should().ContainSingle();
        JsonObject item = result.Items[0]!.AsObject();
        JsonObject keyValues = item["keyValues"]!.AsObject();
        keyValues["namespace"]!.GetValue<string>().Should().Be(ProgramTypeDescriptorNamespace);
        keyValues["codeValue"]!.GetValue<string>().Should().Be(ProgramTypeDescriptorCodeValue);
    }

    [Test]
    public async Task It_suppresses_deleted_descriptors_that_have_been_recreated()
    {
        short resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "ProgramTypeDescriptor");
        long descriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("c0000004-2000-0000-0000-000000000004"),
            resourceKeyId,
            "Ed-Fi:ProgramTypeDescriptor",
            ProgramTypeDescriptorUri,
            ProgramTypeDescriptorNamespace,
            ProgramTypeDescriptorCodeValue,
            ProgramTypeDescriptorCodeValue
        );

        await DeleteDescriptorAsync(descriptorDocumentId);

        await InsertDescriptorAsync(
            Guid.Parse("c0000005-2000-0000-0000-000000000005"),
            resourceKeyId,
            "ProgramTypeDescriptor",
            ProgramTypeDescriptorUri,
            ProgramTypeDescriptorNamespace,
            ProgramTypeDescriptorCodeValue,
            ProgramTypeDescriptorCodeValue
        );

        TrackedChangeQueryResult result = await QueryDescriptorTrackedChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: true,
            changeVersionRange: new ChangeVersionRange(0, long.MaxValue)
        );

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    [Test]
    public async Task It_suppresses_deleted_programs_that_have_been_recreated_with_descriptor_identity()
    {
        long programTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("c0000006-2000-0000-0000-000000000006"),
            await GetResourceKeyIdAsync("Ed-Fi", "ProgramTypeDescriptor"),
            "Ed-Fi:ProgramTypeDescriptor",
            ProgramTypeDescriptorUri,
            ProgramTypeDescriptorNamespace,
            ProgramTypeDescriptorCodeValue,
            ProgramTypeDescriptorCodeValue
        );
        long programDocumentId = await InsertDocumentAsync(
            ProgramDocumentUuid.Value,
            await GetResourceKeyIdAsync("Ed-Fi", "Program")
        );

        await InsertProgramRootAsync(programDocumentId, programTypeDescriptorDocumentId, ProgramName);
        await DeleteProgramRootAsync(programDocumentId);
        await InsertProgramRootAsync(programDocumentId, programTypeDescriptorDocumentId, ProgramName);

        TrackedChangeQueryResult result = await QueryProgramTrackedChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: true
        );

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlBackendIntegrationTestServices();

        short schoolResourceKeyId = _mappingSet.ResourceKeyIdByResource[SchoolResource];
        Dictionary<short, DocumentLinkSlugTriple> slugByResourceKeyId = new()
        {
            [schoolResourceKeyId] = new DocumentLinkSlugTriple(
                ProjectEndpointName: "ed-fi",
                EndpointName: "schools",
                ResourceName: "School"
            ),
        };
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentLinkSlugResolver>(
                new DeterministicLinkSlugResolver(slugByResourceKeyId)
            )
        );
        services.Configure<ResourceLinksOptions>(static options => options.Enabled = true);

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
        EffectiveProjectSchema effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(
            project =>
                string.Equals(
                    project.ProjectEndpointName,
                    projectEndpointName,
                    StringComparison.OrdinalIgnoreCase
                )
        );

        ProjectSchema projectSchema = new(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        JsonNode resourceSchemaNode =
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
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
        );

    private async Task SeedReferenceDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("c0000001-2000-0000-0000-000000000001"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("c0000002-2000-0000-0000-000000000002"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
    }

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        short resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);
        long documentId = await InsertDescriptorAsync(
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

        return documentId;
    }

    private async Task<UpsertResult> UpsertSchoolAsync()
    {
        JsonNode requestBody = CreateSchoolRequestBody();
        return await InvokeDocumentStoreAsync(repository =>
            repository.UpsertDocument(
                new UpsertRequest(
                    ResourceInfo: _schoolResourceInfo,
                    DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                        requestBody,
                        _schoolResourceInfo,
                        _schoolResourceSchema,
                        _mappingSet
                    ),
                    MappingSet: _mappingSet,
                    EdfiDoc: requestBody,
                    Headers: [],
                    TraceId: new TraceId("mssql-change-query-seed-school"),
                    DocumentUuid: SchoolDocumentUuid
                )
            )
        );
    }

    private async Task<UpsertResult> UpsertAcademicWeekAsync(string weekIdentifier)
    {
        JsonNode requestBody = CreateAcademicWeekRequestBody(weekIdentifier);
        return await InvokeDocumentStoreAsync(repository =>
            repository.UpsertDocument(
                new UpsertRequest(
                    ResourceInfo: _academicWeekResourceInfo,
                    DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                        requestBody,
                        _academicWeekResourceInfo,
                        _academicWeekResourceSchema,
                        _mappingSet
                    ),
                    MappingSet: _mappingSet,
                    EdfiDoc: requestBody,
                    Headers: [],
                    TraceId: new TraceId("mssql-change-query-seed-academicweek"),
                    DocumentUuid: AcademicWeekDocumentUuid
                )
            )
        );
    }

    private async Task<TResult> InvokeDocumentStoreAsync<TResult>(
        Func<RelationalDocumentStoreRepository, Task<TResult>> action
    )
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlTrackedChangeQuery",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return await action(scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>());
    }

    private async Task<TrackedChangeQueryResult> QueryAcademicWeekTrackedChangesAsync(
        ChangeQueryEndpointOperation operation,
        bool totalCount
    )
    {
        ConcreteResourceModel resourceModel = _mappingSet.Model.ConcreteResourcesInNameOrder.Single(x =>
            x.RelationalModel.Resource == AcademicWeekResource
        );
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.SourceTable == resourceModel.RelationalModel.Root.Table
        );

        return await QueryTrackedChangesAsync(
            operation,
            totalCount,
            _academicWeekResourceInfo,
            resourceModel,
            trackedTable,
            new TraceId($"mssql-change-query-{operation}-academicweek")
        );
    }

    private async Task<TrackedChangeQueryResult> QueryProgramTrackedChangesAsync(
        ChangeQueryEndpointOperation operation,
        bool totalCount
    )
    {
        ConcreteResourceModel resourceModel = _mappingSet.Model.ConcreteResourcesInNameOrder.Single(x =>
            x.RelationalModel.Resource == ProgramResource
        );
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.SourceTable == resourceModel.RelationalModel.Root.Table
        );

        return await QueryTrackedChangesAsync(
            operation,
            totalCount,
            _programResourceInfo,
            resourceModel,
            trackedTable,
            new TraceId($"mssql-change-query-{operation}-program")
        );
    }

    private async Task<TrackedChangeQueryResult> QueryDescriptorTrackedChangesAsync(
        ChangeQueryEndpointOperation operation,
        bool totalCount,
        ChangeVersionRange? changeVersionRange = null
    )
    {
        ConcreteResourceModel resourceModel = _mappingSet.Model.ConcreteResourcesInNameOrder.Single(x =>
            x.RelationalModel.Resource == DescriptorResource
        );
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.Kind is TrackedChangeTableKind.SharedDescriptor
        );

        return await QueryTrackedChangesAsync(
            operation,
            totalCount,
            _descriptorResourceInfo,
            resourceModel,
            trackedTable,
            new TraceId($"mssql-change-query-{operation}-descriptor"),
            changeVersionRange
        );
    }

    private async Task<TrackedChangeQueryResult> QueryTrackedChangesAsync(
        ChangeQueryEndpointOperation operation,
        bool totalCount,
        ResourceInfo resourceInfo,
        ConcreteResourceModel resourceModel,
        TrackedChangeTableInfo trackedTable,
        TraceId traceId,
        ChangeVersionRange? changeVersionRange = null
    )
    {
        RelationalChangeQueryRepository repository = CreateChangeQueryRepository();
        var request = new TestTrackedChangeQueryRequest(
            ResourceInfo: resourceInfo,
            Operation: operation,
            PaginationParameters: new PaginationParameters(
                Limit: 25,
                Offset: 0,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            ChangeVersionRange: changeVersionRange ?? new ChangeVersionRange(0, long.MaxValue),
            TraceId: traceId,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            AuthorizationStrategyEvaluators: [],
            MappingSet: _mappingSet,
            ResourceModel: resourceModel,
            TrackedChangeTable: trackedTable
        );

        return await repository.QueryTrackedChanges(request);
    }

    private static JsonNode CreateSchoolRequestBody()
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{SchoolId}},
              "nameOfInstitution": "Change Query Test High School",
              "educationOrganizationCategories": [
                {
                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                }
              ],
              "gradeLevels": [
                {
                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                }
              ]
            }
            """
        )!;
    }

    private static JsonNode CreateAcademicWeekRequestBody(string weekIdentifier)
    {
        return JsonNode.Parse(
            $$"""
            {
              "weekIdentifier": "{{weekIdentifier}}",
              "schoolReference": { "schoolId": {{SchoolId}} },
              "beginDate": "2025-08-15",
              "endDate": "2025-08-22",
              "totalInstructionalDays": 5
            }
            """
        )!;
    }

    private async Task<long> GetAcademicWeekDocumentIdAsync()
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", AcademicWeekDocumentUuid.Value)
        );
    }

    private async Task<long> GetSchoolDocumentIdAsync()
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", SchoolDocumentUuid.Value)
        );
    }

    private async Task DeleteAcademicWeekRootAsync(long academicWeekDocumentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [edfi].[AcademicWeek]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", academicWeekDocumentId)
        );
    }

    private async Task InsertAcademicWeekRootAsync(long academicWeekDocumentId, string weekIdentifier)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[AcademicWeek] (
                [DocumentId],
                [School_DocumentId],
                [School_SchoolId],
                [BeginDate],
                [EndDate],
                [TotalInstructionalDays],
                [WeekIdentifier]
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                @beginDate,
                @endDate,
                @totalInstructionalDays,
                @weekIdentifier
            );
            """,
            new SqlParameter("@documentId", academicWeekDocumentId),
            new SqlParameter("@schoolDocumentId", await GetSchoolDocumentIdAsync()),
            new SqlParameter("@schoolId", SchoolId),
            new SqlParameter("@beginDate", new DateTime(2025, 8, 15, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@endDate", new DateTime(2025, 8, 22, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@totalInstructionalDays", 5m),
            new SqlParameter("@weekIdentifier", weekIdentifier)
        );
    }

    private async Task DeleteProgramRootAsync(long programDocumentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [edfi].[Program]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", programDocumentId)
        );
    }

    private async Task InsertProgramRootAsync(
        long programDocumentId,
        long programTypeDescriptorDocumentId,
        string programName
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Program] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [ProgramTypeDescriptor_DescriptorId],
                [ProgramName]
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                @programTypeDescriptorDocumentId,
                @programName
            );
            """,
            new SqlParameter("@documentId", programDocumentId),
            new SqlParameter("@schoolDocumentId", await GetSchoolDocumentIdAsync()),
            new SqlParameter("@schoolId", SchoolId),
            new SqlParameter("@programTypeDescriptorDocumentId", programTypeDescriptorDocumentId),
            new SqlParameter("@programName", programName)
        );
    }

    private async Task UpdateAcademicWeekIdentifierAsync(long academicWeekDocumentId, string weekIdentifier)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[AcademicWeek]
            SET [WeekIdentifier] = @weekIdentifier
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@weekIdentifier", weekIdentifier),
            new SqlParameter("@documentId", academicWeekDocumentId)
        );
    }

    private async Task<(
        long FirstChangeVersion,
        long LatestChangeVersion
    )> GetAcademicWeekKeyChangeVersionsAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT
                MIN([ChangeVersion]) AS [FirstChangeVersion],
                MAX([ChangeVersion]) AS [LatestChangeVersion]
            FROM [tracked_changes_edfi].[AcademicWeek]
            WHERE [Id] = @documentUuid
              AND [NewWeekIdentifier] IS NOT NULL;
            """,
            new SqlParameter("@documentUuid", AcademicWeekDocumentUuid.Value)
        );

        return (
            Convert.ToInt64(rows[0]["FirstChangeVersion"]),
            Convert.ToInt64(rows[0]["LatestChangeVersion"])
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

    private async Task DeleteDescriptorAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Descriptor]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT inserted.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
        long documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [ResourceKeyId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
        );

        return documentId;
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            MERGE INTO [dms].[ReferentialIdentity] AS target
            USING (SELECT @referentialId AS [ReferentialId]) AS source
              ON target.[ReferentialId] = source.[ReferentialId]
            WHEN NOT MATCHED THEN
              INSERT ([ReferentialId], [DocumentId], [ResourceKeyId])
              VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", referentialId.Value),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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

    // ──────────────────────────────────────────────────────────────────────
    // ReadChanges authorization (DMS-1188 / DMS-1197) — SQL Server mirror.
    //
    // See the PostgreSQL fixture for the scenario rationale. Rows and auth-view backing data are
    // seeded directly (tracked-change and auth tables accept direct inserts); a person is made
    // authorized by seeding a tracked StudentSchoolAssociation/responsibility arm of the
    // *IncludingDeletes view plus an auth.EducationOrganizationIdToEducationOrganizationId tuple, so
    // no live person/association rows are required.
    // ──────────────────────────────────────────────────────────────────────

    private const long AuthClaimEdOrgId = 255901001L;
    private const long AuthOtherEdOrgId = 255999999L;
    private const long AuthThirdEdOrgId = 255888888L;
    private const long AuthDirectOnlyEdOrgId = 255777777L;
    private const string AuthNamespacePrefix = "uri://ed-fi.org/";
    private const string CrisisTypeDescriptorDiscriminator = "Ed-Fi:CrisisTypeDescriptor";
    private const string NonMedicalImmunizationExemptionDescriptorDiscriminator =
        "Ed-Fi:NonMedicalImmunizationExemptionDescriptor";

    private static readonly QualifiedResourceName DisciplineActionResource = new("Ed-Fi", "DisciplineAction");
    private static readonly QualifiedResourceName StudentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName GradeResource = new("Ed-Fi", "Grade");
    private static readonly QualifiedResourceName StudentHealthResource = new("Ed-Fi", "StudentHealth");
    private static readonly QualifiedResourceName SurveyResource = new("Ed-Fi", "Survey");
    private static readonly QualifiedResourceName CrisisTypeDescriptorResource = new(
        "Ed-Fi",
        "CrisisTypeDescriptor"
    );
    private static readonly QualifiedResourceName NonMedicalImmunizationExemptionDescriptorResource = new(
        "Ed-Fi",
        "NonMedicalImmunizationExemptionDescriptor"
    );

    // ── EdOrg-only ────────────────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_returns_the_edorg_row_for_an_authorized_claim_and_hides_it_for_others()
    {
        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertAcademicWeekTombstoneAsync("Auth-Week-EdOrg", AuthClaimEdOrgId);

        TrackedChangeQueryResult authorized = await QueryAcademicWeekDeletesAsync(
            [AuthClaimEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );
        TrackedChangeQueryResult unauthorized = await QueryAcademicWeekDeletesAsync(
            [AuthOtherEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        authorized.Items.Should().ContainSingle();
        authorized.Items[0]!["keyValues"]!["weekIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("Auth-Week-EdOrg");
        unauthorized.Items.Should().BeEmpty();
    }

    // DMS-1188 fail-closed (SQL Server): zero claim EdOrg ids + a relationship strategy must yield an
    // EMPTY result (no rows, no exception) — not a 500. The match-nothing predicate is
    // `IN (SELECT 1 WHERE 1 = 0)`; the same seeded row is returned for a real claim.
    [Test]
    public async Task ReadChanges_returns_empty_for_a_relationship_strategy_with_no_claim_edorg_ids()
    {
        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertAcademicWeekTombstoneAsync("Auth-Week-NoClaims", AuthClaimEdOrgId);

        TrackedChangeQueryResult noClaims = await QueryAcademicWeekDeletesAsync(
            [],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );
        TrackedChangeQueryResult withClaim = await QueryAcademicWeekDeletesAsync(
            [AuthClaimEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        noClaims.AuthorizationFailure.Should().BeNull();
        noClaims.Items.Should().BeEmpty();
        withClaim.Items.Should().ContainSingle();
    }

    [Test]
    public async Task ReadChanges_authorizes_an_edorg_row_through_a_hierarchy_ancestor()
    {
        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthOtherEdOrgId);
        await InsertAcademicWeekTombstoneAsync("Auth-Week-Ancestor", AuthOtherEdOrgId);

        TrackedChangeQueryResult viaAncestor = await QueryAcademicWeekDeletesAsync(
            [AuthClaimEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );
        TrackedChangeQueryResult viaDescendant = await QueryAcademicWeekDeletesAsync(
            [AuthOtherEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        viaAncestor.Items.Should().ContainSingle();
        viaDescendant.Items.Should().ContainSingle();
    }

    [Test]
    public async Task ReadChanges_authorizes_an_edorg_row_by_direct_claim_without_hierarchy_self_tuple()
    {
        await InsertAcademicWeekTombstoneAsync("Auth-Week-Direct", AuthDirectOnlyEdOrgId);

        TrackedChangeQueryResult result = await QueryAcademicWeekDeletesAsync(
            [AuthDirectOnlyEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["weekIdentifier"]!.GetValue<string>().Should().Be("Auth-Week-Direct");
    }

    // ── EdOrg-only inverted ───────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_authorizes_inverted_edorg_using_the_swapped_hierarchy_direction()
    {
        await InsertAuthEdOrgTupleAsync(source: AuthClaimEdOrgId, target: AuthOtherEdOrgId);
        await InsertAcademicWeekTombstoneAsync("Auth-Week-Inverted", AuthClaimEdOrgId);

        TrackedChangeQueryResult inverted = await QueryAcademicWeekDeletesAsync(
            [AuthOtherEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
        );
        TrackedChangeQueryResult normal = await QueryAcademicWeekDeletesAsync(
            [AuthOtherEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

        inverted.Items.Should().ContainSingle();
        normal.Items.Should().BeEmpty();
    }

    [Test]
    public async Task ReadChanges_authorizes_inverted_edorg_by_direct_claim_without_hierarchy_self_tuple()
    {
        await InsertAcademicWeekTombstoneAsync("Auth-Week-Inverted-Direct", AuthDirectOnlyEdOrgId);

        TrackedChangeQueryResult result = await QueryAcademicWeekDeletesAsync(
            [AuthDirectOnlyEdOrgId],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["weekIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("Auth-Week-Inverted-Direct");
    }

    // ── Relationships with EdOrgs and people (including deletes) ───────────

    [Test]
    public async Task ReadChanges_filters_a_person_resource_by_relationships_with_edorgs_and_people()
    {
        const long authorizedStudentDocId = 920001L;
        const long unauthorizedStudentDocId = 920002L;

        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertTrackedStudentSchoolAssociationAsync(AuthClaimEdOrgId, authorizedStudentDocId);

        await InsertStudentHealthTombstoneAsync(AuthClaimEdOrgId, authorizedStudentDocId);
        await InsertStudentHealthTombstoneAsync(AuthClaimEdOrgId, unauthorizedStudentDocId);

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            StudentHealthResource,
            [AuthClaimEdOrgId],
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be($"STU{authorizedStudentDocId}");

        TrackedChangeQueryResult unauthorized = await QueryDeletesAsync(
            StudentHealthResource,
            [AuthOtherEdOrgId],
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );
        unauthorized.Items.Should().BeEmpty();
    }

    // ── Relationships with students only (including deletes) ──────────────

    [Test]
    public async Task ReadChanges_filters_a_person_resource_by_relationships_with_students_only()
    {
        const long authorizedStudentDocId = 921001L;
        const long unauthorizedStudentDocId = 921002L;

        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertTrackedStudentSchoolAssociationAsync(AuthClaimEdOrgId, authorizedStudentDocId);

        await InsertDisciplineActionTombstoneAsync("DA-Students-1", authorizedStudentDocId);
        await InsertDisciplineActionTombstoneAsync("DA-Students-2", unauthorizedStudentDocId);

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            DisciplineActionResource,
            [AuthClaimEdOrgId],
            "RelationshipsWithStudentsOnlyIncludingDeletes"
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["disciplineActionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("DA-Students-1");
    }

    // ── Person resource's own tombstone: DocumentId system column (DMS-1193) ──

    [Test]
    public async Task ReadChanges_returns_a_deleted_student_through_its_document_id_system_column()
    {
        // A person resource's own tombstone carries no self person value column; the trigger writes the
        // deleted row's DocumentId into the DocumentId system column and the ReadChanges planner
        // authorizes the self person path through it.
        const string studentUniqueId = "STU-DocumentId-System-Column";
        short studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        long studentDocumentId = await InsertDocumentAsync(Guid.NewGuid(), studentResourceKeyId);
        await InsertStudentRootAsync(studentDocumentId, studentUniqueId);

        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertTrackedStudentSchoolAssociationAsync(AuthClaimEdOrgId, studentDocumentId);

        await DeleteStudentRootAsync(studentDocumentId);

        long trackedDocumentId = await _database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [tracked_changes_edfi].[Student]
            WHERE [OldStudentUniqueId] = @studentUniqueId;
            """,
            new SqlParameter("@studentUniqueId", studentUniqueId)
        );
        trackedDocumentId.Should().Be(studentDocumentId);

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            StudentResource,
            [AuthClaimEdOrgId],
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["studentUniqueId"]!.GetValue<string>().Should().Be(studentUniqueId);

        TrackedChangeQueryResult unauthorized = await QueryDeletesAsync(
            StudentResource,
            [AuthOtherEdOrgId],
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );
        unauthorized.Items.Should().BeEmpty();
    }

    // ── Person DocumentId by natural-key seek under a cascading key change (DMS-1193 Task 44) ──

    [Test]
    public async Task ReadChanges_keychanges_records_the_old_person_when_a_cascade_repoints_the_association()
    {
        // Grade's person column is filled by seeking edfi.Student on the old row's own
        // StudentSectionAssociation_StudentUniqueId. Hopping through the live association would read the
        // already re-pointed row and record student B as the "old" person, hiding the key change from a
        // claim that covers only student A.
        const string studentA = "STU-Seek-A";
        const string studentB = "STU-Seek-B";

        await SeedGradeChainReferenceDataAsync();
        short studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        long studentADocumentId = await InsertDocumentAsync(Guid.NewGuid(), studentResourceKeyId);
        await InsertStudentRootAsync(studentADocumentId, studentA);
        long studentBDocumentId = await InsertDocumentAsync(Guid.NewGuid(), studentResourceKeyId);
        await InsertStudentRootAsync(studentBDocumentId, studentB);
        SectionChainSeed chain = await InsertSectionChainAsync();
        long associationDocumentId = await InsertStudentSectionAssociationRootAsync(
            chain,
            studentADocumentId,
            studentA
        );
        await InsertGradeRootAsync(chain, associationDocumentId, studentA);

        // Only student A is enrolled at the school for the IncludingDeletes people view.
        await InsertAuthEdOrgTupleAsync(SchoolId, SchoolId);
        await InsertTrackedStudentSchoolAssociationAsync(SchoolId, studentADocumentId);

        await RepointStudentSectionAssociationAsync(associationDocumentId, studentBDocumentId, studentB);

        (long oldPersonDocumentId, long newPersonDocumentId) =
            await ReadGradeKeyChangePersonDocumentIdsAsync();
        oldPersonDocumentId.Should().Be(studentADocumentId, "the old row carries student A's unique id");
        newPersonDocumentId.Should().Be(studentBDocumentId, "the new row carries student B's unique id");

        TrackedChangeQueryResult result = await QueryKeyChangesAsync(
            GradeResource,
            [SchoolId],
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes"
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["oldKeyValues"]!["studentUniqueId"]!.GetValue<string>().Should().Be(studentA);
        result.Items[0]!["newKeyValues"]!["studentUniqueId"]!.GetValue<string>().Should().Be(studentB);
    }

    // ── Relationships with students only through responsibility (incl. deletes) ──

    [Test]
    public async Task ReadChanges_filters_a_person_resource_by_students_only_through_responsibility()
    {
        const long authorizedStudentDocId = 922001L;
        const long unauthorizedStudentDocId = 922002L;

        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertTrackedStudentResponsibilityAssociationAsync(AuthClaimEdOrgId, authorizedStudentDocId);

        await InsertDisciplineActionTombstoneAsync("DA-Resp-1", authorizedStudentDocId);
        await InsertDisciplineActionTombstoneAsync("DA-Resp-2", unauthorizedStudentDocId);

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            DisciplineActionResource,
            [AuthClaimEdOrgId],
            "RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["disciplineActionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("DA-Resp-1");
    }

    // ── NamespaceBased (resource) ─────────────────────────────────────────

    [Test]
    public async Task ReadChanges_filters_a_namespace_based_resource_by_prefix_and_hides_mismatches()
    {
        await InsertSurveyTombstoneAsync("Survey-Match", AuthNamespacePrefix + "survey/match");
        await InsertSurveyTombstoneAsync("Survey-Mismatch", "uri://other.org/survey");
        await InsertSurveyTombstoneAsync("Survey-Empty", "");

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            SurveyResource,
            claimEdOrgIds: [],
            namespacePrefixes: [AuthNamespacePrefix],
            strategies: [AuthorizationStrategyNameConstants.NamespaceBased]
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["surveyIdentifier"]!.GetValue<string>().Should().Be("Survey-Match");
    }

    // ── NamespaceBased (descriptor exception) ─────────────────────────────

    [Test]
    public async Task ReadChanges_filters_a_descriptor_by_namespace_prefix_and_discriminator()
    {
        await InsertDescriptorTombstoneAsync(
            CrisisTypeDescriptorDiscriminator,
            AuthNamespacePrefix + "CrisisTypeDescriptor",
            "Lockdown-Match"
        );
        await InsertDescriptorTombstoneAsync(
            CrisisTypeDescriptorDiscriminator,
            "uri://other.org/CrisisTypeDescriptor",
            "Lockdown-Mismatch"
        );
        await InsertDescriptorTombstoneAsync(
            "Ed-Fi:GradeLevelDescriptor",
            AuthNamespacePrefix + "GradeLevelDescriptor",
            "Tenth grade"
        );

        TrackedChangeQueryResult result = await QueryCrisisTypeDescriptorDeletesAsync(
            namespacePrefixes: [AuthNamespacePrefix]
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["codeValue"]!.GetValue<string>().Should().Be("Lockdown-Match");
    }

    [Test]
    public async Task ReadChanges_filters_the_nonmedical_immunization_exemption_descriptor_exception_by_namespace_prefix()
    {
        await InsertDescriptorTombstoneAsync(
            NonMedicalImmunizationExemptionDescriptorDiscriminator,
            AuthNamespacePrefix + "NonMedicalImmunizationExemptionDescriptor",
            "Religious"
        );
        await InsertDescriptorTombstoneAsync(
            NonMedicalImmunizationExemptionDescriptorDiscriminator,
            "uri://other.org/NonMedicalImmunizationExemptionDescriptor",
            "Medical"
        );

        TrackedChangeQueryResult result = await QueryDescriptorDeletesAsync(
            NonMedicalImmunizationExemptionDescriptorResource,
            namespacePrefixes: [AuthNamespacePrefix],
            traceId: new TraceId("mssql-readchanges-nonmedical-immunization-descriptor")
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["codeValue"]!.GetValue<string>().Should().Be("Religious");
    }

    // ── NoFurtherAuthorizationRequired ────────────────────────────────────

    [Test]
    public async Task ReadChanges_returns_all_rows_for_no_further_authorization_required()
    {
        await InsertAcademicWeekTombstoneAsync("NFR-A", AuthClaimEdOrgId);
        await InsertAcademicWeekTombstoneAsync("NFR-B", AuthOtherEdOrgId);

        TrackedChangeQueryResult result = await QueryAcademicWeekDeletesAsync(
            [AuthOtherEdOrgId],
            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
        );

        result.Items.Should().HaveCount(2);
    }

    // ── Unsupported strategy → SecurityConfiguration (500) ────────────────

    [Test]
    public async Task ReadChanges_returns_security_configuration_failure_for_an_unsupported_strategy()
    {
        await InsertAcademicWeekTombstoneAsync("Unsupported", AuthClaimEdOrgId);

        TrackedChangeQueryResult result = await QueryAcademicWeekDeletesAsync(
            [AuthClaimEdOrgId],
            AuthorizationStrategyNameConstants.OwnershipBased
        );

        result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>();
        result.Items.Should().BeEmpty();
    }

    // ── NamespaceBased with no prefixes → NamespaceNoPrefixesConfigured (403) ──

    [Test]
    public async Task ReadChanges_returns_no_prefixes_failure_when_namespace_based_has_no_prefixes()
    {
        await InsertSurveyTombstoneAsync("NoPrefix", AuthNamespacePrefix + "survey");

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            SurveyResource,
            claimEdOrgIds: [],
            namespacePrefixes: [],
            strategies: [AuthorizationStrategyNameConstants.NamespaceBased]
        );

        result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.NamespaceNoPrefixesConfigured>();
        result.Items.Should().BeEmpty();
    }

    // ── Paging + totalCount with predicates applied before paging ─────────

    [Test]
    public async Task ReadChanges_applies_authorization_before_paging_and_total_count()
    {
        const int authorizedCount = 5;
        const int unauthorizedCount = 3;

        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        for (int i = 0; i < authorizedCount; i++)
        {
            await InsertAcademicWeekTombstoneAsync($"Paged-Auth-{i:D2}", AuthClaimEdOrgId);
        }
        for (int i = 0; i < unauthorizedCount; i++)
        {
            await InsertAcademicWeekTombstoneAsync($"Paged-Unauth-{i:D2}", AuthOtherEdOrgId);
        }

        TrackedChangeQueryResult page = await QueryAcademicWeekDeletesAsync(
            [AuthClaimEdOrgId],
            strategies: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            limit: 2,
            offset: 0,
            totalCount: true
        );

        page.TotalCount.Should().Be(authorizedCount);
        page.Items.Should().HaveCount(2);
        page.Items.Should()
            .OnlyContain(item =>
                item!["keyValues"]!["weekIdentifier"]!.GetValue<string>().StartsWith("Paged-Auth-")
            );

        TrackedChangeQueryResult secondPage = await QueryAcademicWeekDeletesAsync(
            [AuthClaimEdOrgId],
            strategies: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            limit: 2,
            offset: 4,
            totalCount: true
        );

        secondPage.TotalCount.Should().Be(authorizedCount);
        secondPage.Items.Should().ContainSingle();
    }

    // ── KeyChanges authorization ─────────────────────────────────────────

    [Test]
    public async Task ReadChanges_filters_keychanges_before_paging_and_total_count()
    {
        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertAcademicWeekKeyChangeAsync(
            "KeyChange-Authorized-Old",
            "KeyChange-Authorized-New",
            AuthClaimEdOrgId
        );
        await InsertAcademicWeekKeyChangeAsync(
            "KeyChange-Unauthorized-Old",
            "KeyChange-Unauthorized-New",
            AuthOtherEdOrgId
        );

        TrackedChangeQueryResult result = await QueryAcademicWeekKeyChangesAsync(
            [AuthClaimEdOrgId],
            [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly],
            totalCount: true
        );

        result.TotalCount.Should().Be(1L);
        result.Items.Should().ContainSingle();
        JsonObject item = result.Items[0]!.AsObject();
        item["oldKeyValues"]!["weekIdentifier"]!.GetValue<string>().Should().Be("KeyChange-Authorized-Old");
        item["newKeyValues"]!["weekIdentifier"]!.GetValue<string>().Should().Be("KeyChange-Authorized-New");
    }

    [Test]
    public async Task ReadChanges_returns_security_configuration_failure_for_keychanges_unsupported_strategy()
    {
        await InsertAcademicWeekKeyChangeAsync(
            "KeyChange-Unsupported-Old",
            "KeyChange-Unsupported-New",
            AuthClaimEdOrgId
        );

        TrackedChangeQueryResult result = await QueryAcademicWeekKeyChangesAsync(
            [AuthClaimEdOrgId],
            [AuthorizationStrategyNameConstants.OwnershipBased],
            totalCount: true
        );

        result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>();
        result.TotalCount.Should().BeNull();
        result.Items.Should().BeEmpty();
    }

    // ── Strategy composition ──────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_ORs_multiple_relationship_strategies()
    {
        await InsertAuthEdOrgTupleAsync(source: AuthClaimEdOrgId, target: AuthOtherEdOrgId);
        await InsertAuthEdOrgTupleAsync(source: AuthThirdEdOrgId, target: AuthClaimEdOrgId);
        await InsertAcademicWeekTombstoneAsync("Composition-Normal", AuthOtherEdOrgId);
        await InsertAcademicWeekTombstoneAsync("Composition-Inverted", AuthThirdEdOrgId);

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            AcademicWeekResource,
            [AuthClaimEdOrgId],
            namespacePrefixes: [],
            strategies:
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            ]
        );

        result.Items.Should().HaveCount(2);
        result
            .Items.Select(item => item!["keyValues"]!["weekIdentifier"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo("Composition-Normal", "Composition-Inverted");
    }

    [Test]
    public async Task ReadChanges_keeps_NoFurtherAuthorizationRequired_noop_when_combined_with_relationship_strategy()
    {
        await InsertAuthEdOrgTupleAsync(AuthClaimEdOrgId, AuthClaimEdOrgId);
        await InsertAcademicWeekTombstoneAsync("NoFurther-With-Relationship-Authorized", AuthClaimEdOrgId);
        await InsertAcademicWeekTombstoneAsync("NoFurther-With-Relationship-Unauthorized", AuthOtherEdOrgId);

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            AcademicWeekResource,
            [AuthClaimEdOrgId],
            namespacePrefixes: [],
            strategies:
            [
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            ]
        );

        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["weekIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("NoFurther-With-Relationship-Authorized");
    }

    // ── Authorization query helpers ───────────────────────────────────────

    private Task<TrackedChangeQueryResult> QueryAcademicWeekDeletesAsync(
        long[] claimEdOrgIds,
        string strategies,
        int limit = 25,
        int offset = 0,
        bool totalCount = false
    ) =>
        QueryDeletesAsync(
            AcademicWeekResource,
            claimEdOrgIds,
            namespacePrefixes: [],
            strategies: [strategies],
            limit: limit,
            offset: offset,
            totalCount: totalCount
        );

    private Task<TrackedChangeQueryResult> QueryAcademicWeekKeyChangesAsync(
        IReadOnlyList<long> claimEdOrgIds,
        IReadOnlyList<string> strategies,
        bool totalCount
    )
    {
        ConcreteResourceModel resourceModel = ResolveResourceModel(AcademicWeekResource);
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.SourceTable == resourceModel.RelationalModel.Root.Table
        );

        return QueryTrackedChangesWithAuthorizationAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            _academicWeekResourceInfo,
            resourceModel,
            trackedTable,
            claimEdOrgIds,
            namespacePrefixes: [],
            strategies,
            limit: 25,
            offset: 0,
            totalCount,
            changeVersionRange: new ChangeVersionRange(0, long.MaxValue),
            traceId: new TraceId("mssql-readchanges-keychanges-academicweek")
        );
    }

    private Task<TrackedChangeQueryResult> QueryCrisisTypeDescriptorDeletesAsync(
        IReadOnlyList<string> namespacePrefixes
    ) =>
        QueryDescriptorDeletesAsync(
            CrisisTypeDescriptorResource,
            namespacePrefixes,
            new TraceId("mssql-readchanges-crisis-descriptor")
        );

    private Task<TrackedChangeQueryResult> QueryDescriptorDeletesAsync(
        QualifiedResourceName descriptorResource,
        IReadOnlyList<string> namespacePrefixes,
        TraceId traceId
    )
    {
        ConcreteResourceModel resourceModel = ResolveResourceModel(DescriptorResource);
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.Kind is TrackedChangeTableKind.SharedDescriptor
        );
        ResourceInfo resourceInfo = ResolveResourceInfo(descriptorResource);

        return QueryTrackedChangesWithAuthorizationAsync(
            ChangeQueryEndpointOperation.Deletes,
            resourceInfo,
            resourceModel,
            trackedTable,
            claimEdOrgIds: [],
            namespacePrefixes: namespacePrefixes,
            strategies: [AuthorizationStrategyNameConstants.NamespaceBased],
            limit: 25,
            offset: 0,
            totalCount: false,
            changeVersionRange: new ChangeVersionRange(0, long.MaxValue),
            traceId
        );
    }

    private Task<TrackedChangeQueryResult> QueryDeletesAsync(
        QualifiedResourceName resource,
        long[] claimEdOrgIds,
        params string[] strategies
    ) => QueryDeletesAsync(resource, claimEdOrgIds, [], strategies);

    private Task<TrackedChangeQueryResult> QueryDeletesAsync(
        QualifiedResourceName resource,
        IReadOnlyList<long> claimEdOrgIds,
        IReadOnlyList<string> namespacePrefixes,
        IReadOnlyList<string> strategies,
        int limit = 25,
        int offset = 0,
        bool totalCount = false
    )
    {
        ConcreteResourceModel resourceModel = ResolveResourceModel(resource);
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.SourceTable == resourceModel.RelationalModel.Root.Table
        );
        ResourceInfo resourceInfo = ResolveResourceInfo(resource);

        return QueryTrackedChangesWithAuthorizationAsync(
            ChangeQueryEndpointOperation.Deletes,
            resourceInfo,
            resourceModel,
            trackedTable,
            claimEdOrgIds,
            namespacePrefixes,
            strategies,
            limit,
            offset,
            totalCount,
            changeVersionRange: new ChangeVersionRange(0, long.MaxValue),
            traceId: new TraceId($"mssql-readchanges-{resource.ResourceName}")
        );
    }

    private async Task<TrackedChangeQueryResult> QueryTrackedChangesWithAuthorizationAsync(
        ChangeQueryEndpointOperation operation,
        ResourceInfo resourceInfo,
        ConcreteResourceModel resourceModel,
        TrackedChangeTableInfo trackedTable,
        IReadOnlyList<long> claimEdOrgIds,
        IReadOnlyList<string> namespacePrefixes,
        IReadOnlyList<string> strategies,
        int limit,
        int offset,
        bool totalCount,
        ChangeVersionRange changeVersionRange,
        TraceId traceId
    )
    {
        RelationalChangeQueryRepository repository = CreateChangeQueryRepository();
        var request = new TestTrackedChangeQueryRequest(
            ResourceInfo: resourceInfo,
            Operation: operation,
            PaginationParameters: new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            ChangeVersionRange: changeVersionRange,
            TraceId: traceId,
            AuthorizationContext: new RelationalAuthorizationContext(claimEdOrgIds, namespacePrefixes),
            AuthorizationStrategyEvaluators:
            [
                .. strategies.Select(name => new AuthorizationStrategyEvaluator(
                    name,
                    [],
                    FilterOperator.And
                )),
            ],
            MappingSet: _mappingSet,
            ResourceModel: resourceModel,
            TrackedChangeTable: trackedTable
        );

        return await repository.QueryTrackedChanges(request);
    }

    private ConcreteResourceModel ResolveResourceModel(QualifiedResourceName resource) =>
        _mappingSet.Model.ConcreteResourcesInNameOrder.Single(x => x.RelationalModel.Resource == resource);

    private ResourceInfo ResolveResourceInfo(QualifiedResourceName resource)
    {
        (ProjectSchema projectSchema, ResourceSchema resourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            resource.ResourceName
        );
        return CreateResourceInfo(projectSchema, resourceSchema);
    }

    // ── Authorization seed helpers ────────────────────────────────────────

    // ── Grade chain seeding (DMS-1193 Task 44) ──────────────────────────

    private const int SeekSchoolYear = 2025;
    private const string SeekCourseCode = "SEEK-101";
    private const string SeekSessionName = "Seek Fall";
    private const string SeekLocalCourseCode = "SEEK-101-L";
    private const string SeekSectionIdentifier = "SEEK-SEC-1";
    private const string SeekGradingPeriodName = "Seek GP1";
    private const string SeekTermDescriptorUri = "uri://ed-fi.org/TermDescriptor#Seek Fall Semester";
    private const string SeekGradingPeriodDescriptorUri =
        "uri://ed-fi.org/GradingPeriodDescriptor#Seek First Six Weeks";
    private const string SeekGradeTypeDescriptorUri = "uri://ed-fi.org/GradeTypeDescriptor#Seek Final";
    private static readonly DateTime SeekBeginDate = new(2024, 8, 20, 0, 0, 0, DateTimeKind.Unspecified);

    private async Task SeedGradeChainReferenceDataAsync()
    {
        short schoolYearResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        long schoolYearDocumentId = await InsertDocumentAsync(Guid.NewGuid(), schoolYearResourceKeyId);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SchoolYearType] ([DocumentId], [CurrentSchoolYear], [SchoolYear], [SchoolYearDescription])
            VALUES (@documentId, 1, @schoolYear, 'Seek 2024-2025');
            """,
            new SqlParameter("@documentId", schoolYearDocumentId),
            new SqlParameter("@schoolYear", SeekSchoolYear)
        );

        await SeedDescriptorAsync(
            Guid.NewGuid(),
            "TermDescriptor",
            "Ed-Fi:TermDescriptor",
            SeekTermDescriptorUri,
            "uri://ed-fi.org/TermDescriptor",
            "Seek Fall Semester",
            "Seek Fall Semester"
        );
        await SeedDescriptorAsync(
            Guid.NewGuid(),
            "GradingPeriodDescriptor",
            "Ed-Fi:GradingPeriodDescriptor",
            SeekGradingPeriodDescriptorUri,
            "uri://ed-fi.org/GradingPeriodDescriptor",
            "Seek First Six Weeks",
            "Seek First Six Weeks"
        );
        await SeedDescriptorAsync(
            Guid.NewGuid(),
            "GradeTypeDescriptor",
            "Ed-Fi:GradeTypeDescriptor",
            SeekGradeTypeDescriptorUri,
            "uri://ed-fi.org/GradeTypeDescriptor",
            "Seek Final",
            "Seek Final"
        );
    }

    private static string DescriptorDocumentIdSql(string resourceName, string uriParameter) =>
        $"""
            (SELECT descriptor.[DocumentId]
             FROM [dms].[Descriptor] descriptor
             INNER JOIN [dms].[Document] document ON document.[DocumentId] = descriptor.[DocumentId]
             INNER JOIN [dms].[ResourceKey] resourceKey ON resourceKey.[ResourceKeyId] = document.[ResourceKeyId]
             WHERE resourceKey.[ProjectName] = 'Ed-Fi' AND resourceKey.[ResourceName] = '{resourceName}'
               AND descriptor.[Uri] = {uriParameter})
            """;

    private const string SchoolYearDocumentIdSql =
        """(SELECT schoolYear.[DocumentId] FROM [edfi].[SchoolYearType] schoolYear WHERE schoolYear.[SchoolYear] = @schoolYear)""";

    /// <summary>
    /// Seeds the singleton parent chain a Grade needs: Course, Session, CourseOffering, Section, and
    /// GradingPeriod, all on the fixture school and school year (mirrors the query-authorization volume
    /// generator's column lists; generated columns are omitted).
    /// </summary>
    /// <summary>
    /// The parent chain a Section (and therefore a StudentSectionAssociation and a Grade) hangs off:
    /// the school it belongs to, its CourseOffering, the first Section, and the identity values the
    /// dependents copy into their reference columns.
    /// </summary>
    private sealed record SectionChainSeed(
        long SchoolDocumentId,
        long ChainSchoolId,
        long CourseOfferingDocumentId,
        long SectionDocumentId,
        string LocalCourseCode,
        string SessionName,
        string SectionIdentifier,
        string GradingPeriodName
    );

    /// <summary>
    /// Seeds the singleton parent chain a Grade needs on the fixture school: see the overload below.
    /// </summary>
    private async Task<SectionChainSeed> InsertSectionChainAsync() =>
        await InsertSectionChainAsync(await GetSchoolDocumentIdAsync(), SchoolId, suffix: "");

    /// <summary>
    /// Seeds the parent chain a Grade needs: Course, Session, CourseOffering, Section, and GradingPeriod,
    /// all on the given school and the fixture school year (mirrors the query-authorization volume
    /// generator's column lists; generated columns are omitted). The suffix keeps a second chain's
    /// natural keys distinct from the first one's.
    /// </summary>
    private async Task<SectionChainSeed> InsertSectionChainAsync(
        long schoolDocumentId,
        long schoolId,
        string suffix
    )
    {
        string courseCode = SeekCourseCode + suffix;
        string sessionName = SeekSessionName + suffix;
        string localCourseCode = SeekLocalCourseCode + suffix;
        string sectionIdentifier = SeekSectionIdentifier + suffix;
        string gradingPeriodName = SeekGradingPeriodName + suffix;

        long courseDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "Course")
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Course] ([DocumentId], [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId], [CourseCode], [CourseTitle], [NumberOfParts])
            VALUES (@documentId, @schoolDocumentId, @schoolId, @courseCode, 'Seek Course', 1);
            """,
            new SqlParameter("@documentId", courseDocumentId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@courseCode", courseCode)
        );

        long sessionDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "Session")
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            INSERT INTO [edfi].[Session] ([DocumentId], [SchoolYear_DocumentId], [SchoolYear_SchoolYear],
                [School_DocumentId], [School_SchoolId], [TermDescriptor_DescriptorId], [BeginDate], [EndDate],
                [SessionName], [TotalInstructionalDays])
            VALUES (@documentId, {SchoolYearDocumentIdSql}, @schoolYear, @schoolDocumentId, @schoolId,
                {DescriptorDocumentIdSql("TermDescriptor", "@termDescriptorUri")}, CAST('2024-08-01' AS date),
                CAST('2024-12-20' AS date), @sessionName, 90);
            """,
            new SqlParameter("@documentId", sessionDocumentId),
            new SqlParameter("@schoolYear", SeekSchoolYear),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@termDescriptorUri", SeekTermDescriptorUri),
            new SqlParameter("@sessionName", sessionName)
        );

        long courseOfferingDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "CourseOffering")
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[CourseOffering] ([DocumentId], [SchoolId_Unified], [Course_DocumentId],
                [Course_CourseCode], [Course_EducationOrganizationId], [School_DocumentId], [Session_DocumentId],
                [Session_SchoolYear], [Session_SessionName], [LocalCourseCode])
            VALUES (@documentId, @schoolId, @courseDocumentId, @courseCode, @schoolId, @schoolDocumentId,
                @sessionDocumentId, @schoolYear, @sessionName, @localCourseCode);
            """,
            new SqlParameter("@documentId", courseOfferingDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@courseDocumentId", courseDocumentId),
            new SqlParameter("@courseCode", courseCode),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@sessionDocumentId", sessionDocumentId),
            new SqlParameter("@schoolYear", SeekSchoolYear),
            new SqlParameter("@sessionName", sessionName),
            new SqlParameter("@localCourseCode", localCourseCode)
        );

        long sectionDocumentId = await InsertSectionRootAsync(
            schoolId,
            courseOfferingDocumentId,
            localCourseCode,
            sessionName,
            sectionIdentifier
        );

        long gradingPeriodDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "GradingPeriod")
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            INSERT INTO [edfi].[GradingPeriod] ([DocumentId], [SchoolYear_DocumentId], [SchoolYear_SchoolYear],
                [School_DocumentId], [School_SchoolId], [GradingPeriodDescriptor_DescriptorId], [BeginDate],
                [EndDate], [GradingPeriodName], [TotalInstructionalDays])
            VALUES (@documentId, {SchoolYearDocumentIdSql}, @schoolYear, @schoolDocumentId, @schoolId,
                {DescriptorDocumentIdSql("GradingPeriodDescriptor", "@gradingPeriodDescriptorUri")},
                CAST('2024-08-01' AS date), CAST('2024-09-13' AS date), @gradingPeriodName, 30);
            """,
            new SqlParameter("@documentId", gradingPeriodDocumentId),
            new SqlParameter("@schoolYear", SeekSchoolYear),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@gradingPeriodDescriptorUri", SeekGradingPeriodDescriptorUri),
            new SqlParameter("@gradingPeriodName", gradingPeriodName)
        );

        return new SectionChainSeed(
            schoolDocumentId,
            schoolId,
            courseOfferingDocumentId,
            sectionDocumentId,
            localCourseCode,
            sessionName,
            sectionIdentifier,
            gradingPeriodName
        );
    }

    private async Task<long> InsertSectionRootAsync(
        long schoolId,
        long courseOfferingDocumentId,
        string localCourseCode,
        string sessionName,
        string sectionIdentifier
    )
    {
        long sectionDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "Section")
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Section] ([DocumentId], [SchoolId_Unified], [CourseOffering_DocumentId],
                [CourseOffering_LocalCourseCode], [CourseOffering_SchoolYear], [CourseOffering_SessionName],
                [SectionIdentifier])
            VALUES (@documentId, @schoolId, @courseOfferingDocumentId, @localCourseCode, @schoolYear,
                @sessionName, @sectionIdentifier);
            """,
            new SqlParameter("@documentId", sectionDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@courseOfferingDocumentId", courseOfferingDocumentId),
            new SqlParameter("@localCourseCode", localCourseCode),
            new SqlParameter("@schoolYear", SeekSchoolYear),
            new SqlParameter("@sessionName", sessionName),
            new SqlParameter("@sectionIdentifier", sectionIdentifier)
        );
        return sectionDocumentId;
    }

    private async Task<long> InsertStudentSectionAssociationRootAsync(
        SectionChainSeed chain,
        long studentDocumentId,
        string studentUniqueId
    )
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "StudentSectionAssociation")
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[StudentSectionAssociation] ([DocumentId], [Section_DocumentId],
                [Section_LocalCourseCode], [Section_SchoolId], [Section_SchoolYear], [Section_SessionName],
                [Section_SectionIdentifier], [Student_DocumentId], [Student_StudentUniqueId], [BeginDate])
            SELECT @documentId, section.[DocumentId], section.[CourseOffering_LocalCourseCode],
                section.[SchoolId_Unified], section.[CourseOffering_SchoolYear],
                section.[CourseOffering_SessionName], section.[SectionIdentifier], @studentDocumentId,
                @studentUniqueId, @beginDate
            FROM [edfi].[Section] section
            WHERE section.[DocumentId] = @sectionDocumentId;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@beginDate", SeekBeginDate),
            new SqlParameter("@sectionDocumentId", chain.SectionDocumentId)
        );
        return documentId;
    }

    private async Task<long> InsertGradeRootAsync(
        SectionChainSeed chain,
        long associationDocumentId,
        string studentUniqueId
    )
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "Grade")
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            INSERT INTO [edfi].[Grade] ([DocumentId], [SchoolId_Unified], [SchoolYear_Unified],
                [GradingPeriodGradingPeriod_DocumentId],
                [GradingPeriodGradingPeriod_GradingPeriodDescriptor_DescriptorId],
                [GradingPeriodGradingPeriod_GradingPeriodName], [StudentSectionAssociation_DocumentId],
                [StudentSectionAssociation_BeginDate], [StudentSectionAssociation_LocalCourseCode],
                [StudentSectionAssociation_SectionIdentifier], [StudentSectionAssociation_SessionName],
                [StudentSectionAssociation_StudentUniqueId], [GradeTypeDescriptor_DescriptorId])
            SELECT @documentId, @schoolId, @schoolYear, gradingPeriod.[DocumentId],
                gradingPeriod.[GradingPeriodDescriptor_DescriptorId], gradingPeriod.[GradingPeriodName],
                @associationDocumentId, @beginDate, @localCourseCode, @sectionIdentifier, @sessionName,
                @studentUniqueId, {DescriptorDocumentIdSql("GradeTypeDescriptor", "@gradeTypeDescriptorUri")}
            FROM [edfi].[GradingPeriod] gradingPeriod
            WHERE gradingPeriod.[GradingPeriodName] = @gradingPeriodName;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", chain.ChainSchoolId),
            new SqlParameter("@schoolYear", SeekSchoolYear),
            new SqlParameter("@associationDocumentId", associationDocumentId),
            new SqlParameter("@beginDate", SeekBeginDate),
            new SqlParameter("@localCourseCode", chain.LocalCourseCode),
            new SqlParameter("@sectionIdentifier", chain.SectionIdentifier),
            new SqlParameter("@sessionName", chain.SessionName),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@gradeTypeDescriptorUri", SeekGradeTypeDescriptorUri),
            new SqlParameter("@gradingPeriodName", chain.GradingPeriodName)
        );
        return documentId;
    }

    /// <summary>
    /// Re-points the association to another student the way an identity update lands in the root table:
    /// both binding columns change together, and the composite FK with ON UPDATE CASCADE carries the new
    /// unique id into Grade, firing Grade's key-change trigger.
    /// </summary>
    private async Task RepointStudentSectionAssociationAsync(
        long associationDocumentId,
        long newStudentDocumentId,
        string newStudentUniqueId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StudentSectionAssociation]
            SET [Student_DocumentId] = @studentDocumentId, [Student_StudentUniqueId] = @studentUniqueId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@studentDocumentId", newStudentDocumentId),
            new SqlParameter("@studentUniqueId", newStudentUniqueId),
            new SqlParameter("@documentId", associationDocumentId)
        );
    }

    private async Task<(
        long OldPersonDocumentId,
        long NewPersonDocumentId
    )> ReadGradeKeyChangePersonDocumentIdsAsync()
    {
        long oldPerson = await _database.ExecuteScalarAsync<long>(
            """
            SELECT [OldStudentSectionAssociation_Student_DocumentId]
            FROM [tracked_changes_edfi].[Grade]
            WHERE [NewStudentSectionAssociation_StudentUniqueId] IS NOT NULL;
            """
        );
        long newPerson = await _database.ExecuteScalarAsync<long>(
            """
            SELECT [NewStudentSectionAssociation_Student_DocumentId]
            FROM [tracked_changes_edfi].[Grade]
            WHERE [NewStudentSectionAssociation_StudentUniqueId] IS NOT NULL;
            """
        );
        return (oldPerson, newPerson);
    }

    private Task<TrackedChangeQueryResult> QueryKeyChangesAsync(
        QualifiedResourceName resource,
        long[] claimEdOrgIds,
        params string[] strategies
    )
    {
        ConcreteResourceModel resourceModel = ResolveResourceModel(resource);
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.SourceTable == resourceModel.RelationalModel.Root.Table
        );

        return QueryTrackedChangesWithAuthorizationAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            ResolveResourceInfo(resource),
            resourceModel,
            trackedTable,
            claimEdOrgIds,
            namespacePrefixes: [],
            strategies,
            limit: 25,
            offset: 0,
            totalCount: false,
            changeVersionRange: new ChangeVersionRange(0, long.MaxValue),
            traceId: new TraceId($"mssql-readchanges-keychanges-{resource.ResourceName}")
        );
    }

    private async Task InsertStudentRootAsync(long studentDocumentId, string studentUniqueId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Student] ([DocumentId], [BirthDate], [FirstName], [LastSurname], [StudentUniqueId])
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new SqlParameter("@documentId", studentDocumentId),
            new SqlParameter("@birthDate", new DateTime(2010, 5, 14, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@firstName", "Tracked"),
            new SqlParameter("@lastSurname", "Student"),
            new SqlParameter("@studentUniqueId", studentUniqueId)
        );
    }

    private async Task DeleteStudentRootAsync(long studentDocumentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [edfi].[Student]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", studentDocumentId)
        );
    }

    private async Task InsertAuthEdOrgTupleAsync(long source, long target)
    {
        await _database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (
                SELECT 1 FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
                WHERE [SourceEducationOrganizationId] = @source
                  AND [TargetEducationOrganizationId] = @target
            )
            INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId]
                ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
            VALUES (@source, @target);
            """,
            new SqlParameter("@source", source),
            new SqlParameter("@target", target)
        );
    }

    private long _nextAuthChangeVersion = 100_000L;

    private long NextAuthChangeVersion() => _nextAuthChangeVersion++;

    // Seeded tombstones stand in for deleted documents that never existed in dms.Document; the DocumentId
    // system column is NOT NULL, so each seeded row gets a distinct synthetic value.
    private long _nextSeededDocumentId = 900_000_000L;

    private long NextSeededDocumentId() => _nextSeededDocumentId++;

    private async Task InsertAcademicWeekTombstoneAsync(string weekIdentifier, long oldSchoolId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[AcademicWeek]
                ([OldSchool_SchoolId], [NewSchool_SchoolId], [OldWeekIdentifier], [NewWeekIdentifier],
                 [Id], [ChangeVersion], [DocumentId])
            VALUES (@oldSchoolId, NULL, @weekIdentifier, NULL, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@oldSchoolId", oldSchoolId),
            new SqlParameter("@weekIdentifier", weekIdentifier),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertAcademicWeekKeyChangeAsync(
        string oldWeekIdentifier,
        string newWeekIdentifier,
        long oldSchoolId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[AcademicWeek]
                ([OldSchool_SchoolId], [NewSchool_SchoolId], [OldWeekIdentifier], [NewWeekIdentifier],
                 [Id], [ChangeVersion], [DocumentId])
            VALUES (@oldSchoolId, @newSchoolId, @oldWeekIdentifier, @newWeekIdentifier, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@oldSchoolId", oldSchoolId),
            new SqlParameter("@newSchoolId", oldSchoolId),
            new SqlParameter("@oldWeekIdentifier", oldWeekIdentifier),
            new SqlParameter("@newWeekIdentifier", newWeekIdentifier),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertSurveyTombstoneAsync(string surveyIdentifier, string oldNamespace)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[Survey]
                ([OldNamespace], [NewNamespace], [OldSurveyIdentifier], [NewSurveyIdentifier],
                 [Id], [ChangeVersion], [DocumentId])
            VALUES (@oldNamespace, NULL, @surveyIdentifier, NULL, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@oldNamespace", oldNamespace),
            new SqlParameter("@surveyIdentifier", surveyIdentifier),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertDescriptorTombstoneAsync(
        string discriminator,
        string oldNamespace,
        string oldCodeValue
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[Descriptor]
                ([OldNamespace], [NewNamespace], [OldCodeValue], [NewCodeValue], [Discriminator],
                 [Id], [ChangeVersion], [DocumentId])
            VALUES (@oldNamespace, NULL, @oldCodeValue, NULL, @discriminator, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@oldNamespace", oldNamespace),
            new SqlParameter("@oldCodeValue", oldCodeValue),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertTrackedStudentSchoolAssociationAsync(long oldSchoolId, long oldStudentDocId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[StudentSchoolAssociation]
                ([OldEntryDate], [NewEntryDate], [OldSchoolId_Unified], [NewSchoolId_Unified],
                 [OldStudent_StudentUniqueId], [NewStudent_StudentUniqueId],
                 [OldStudent_DocumentId], [NewStudent_DocumentId], [Id], [ChangeVersion], [DocumentId])
            VALUES (@entryDate, NULL, @oldSchoolId, NULL, @studentUniqueId, NULL, @oldStudentDocId, NULL,
                    @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@entryDate", new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@oldSchoolId", oldSchoolId),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertTrackedStudentResponsibilityAssociationAsync(
        long oldEdOrgId,
        long oldStudentDocId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[StudentEducationOrganizationResponsibilityAssociation]
                ([OldBeginDate], [NewBeginDate],
                 [OldEducationOrganization_EducationOrganizationId], [NewEducationOrganization_EducationOrganizationId],
                 [OldResponsibilityDescriptor_Namespace], [NewResponsibilityDescriptor_Namespace],
                 [OldResponsibilityDescriptor_CodeValue], [NewResponsibilityDescriptor_CodeValue],
                 [OldStudent_StudentUniqueId], [NewStudent_StudentUniqueId],
                 [OldStudent_DocumentId], [NewStudent_DocumentId], [Id], [ChangeVersion], [DocumentId])
            VALUES (@beginDate, NULL, @oldEdOrgId, NULL, @respNamespace, NULL, @respCodeValue, NULL,
                    @studentUniqueId, NULL, @oldStudentDocId, NULL, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@beginDate", new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@oldEdOrgId", oldEdOrgId),
            new SqlParameter("@respNamespace", "uri://ed-fi.org/ResponsibilityDescriptor"),
            new SqlParameter("@respCodeValue", "Educational"),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertStudentHealthTombstoneAsync(long oldEdOrgId, long oldStudentDocId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[StudentHealth]
                ([OldEducationOrganization_EducationOrganizationId], [NewEducationOrganization_EducationOrganizationId],
                 [OldStudent_StudentUniqueId], [NewStudent_StudentUniqueId],
                 [OldStudent_DocumentId], [NewStudent_DocumentId], [Id], [ChangeVersion], [DocumentId])
            VALUES (@oldEdOrgId, NULL, @studentUniqueId, NULL, @oldStudentDocId, NULL, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@oldEdOrgId", oldEdOrgId),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    private async Task InsertDisciplineActionTombstoneAsync(
        string disciplineActionIdentifier,
        long oldStudentDocId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[DisciplineAction]
                ([OldDisciplineActionIdentifier], [NewDisciplineActionIdentifier],
                 [OldDisciplineDate], [NewDisciplineDate],
                 [OldStudent_StudentUniqueId], [NewStudent_StudentUniqueId],
                 [OldResponsibilitySchool_SchoolId], [NewResponsibilitySchool_SchoolId],
                 [OldStudent_DocumentId], [NewStudent_DocumentId], [Id], [ChangeVersion], [DocumentId])
            VALUES (@identifier, NULL, @disciplineDate, NULL, @studentUniqueId, NULL, @schoolId, NULL,
                    @oldStudentDocId, NULL, @id, @changeVersion, @documentId);
            """,
            new SqlParameter("@identifier", disciplineActionIdentifier),
            new SqlParameter("@disciplineDate", new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@schoolId", AuthClaimEdOrgId),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion()),
            new SqlParameter("@documentId", NextSeededDocumentId())
        );
    }

    // ──────────────────────────────────────────────────────────────────────
    // Custom view-based ReadChanges authorization (DMS-1193 Tasks 45-48) — SQL Server mirror.
    //
    // See the PostgreSQL fixture for the scenario rationale. Live rows are inserted through the root
    // tables, deletes and identity updates fire the generated tombstone / key-change triggers, and the
    // custom auth views are created in [auth] the way an implementer would author them (a live arm over
    // the basis table, optionally unioned with the basis tombstone table for *IncludingDeletes views).
    // Every request asks for totalCount and a ChangeVersion window bounded to the scenario's own changes.
    // ──────────────────────────────────────────────────────────────────────

    private const long AlternativeSchoolId = 255901101L;
    private const long RegularSchoolId = 255901102L;
    private const string SchoolTypeDescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string AlternativeSchoolTypeUri = SchoolTypeDescriptorNamespace + "#Alternative";
    private const string RegularSchoolTypeUri = SchoolTypeDescriptorNamespace + "#Regular";
    private const string EntryGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string SchoolWithAlternativeTypeStrategy = "SchoolWithAlternativeType";
    private const string CustomViewBasisNotIdentifyingOrSecurableHintFragment =
        "neither an identifying property nor a securable element";
    private static readonly DateTime CustomViewEntryDate = new(
        2025,
        8,
        18,
        0,
        0,
        0,
        DateTimeKind.Unspecified
    );

    private static readonly QualifiedResourceName StudentSchoolAssociationResource = new(
        "Ed-Fi",
        "StudentSchoolAssociation"
    );
    private static readonly QualifiedResourceName SectionResource = new("Ed-Fi", "Section");
    private static readonly QualifiedResourceName StudentAssessmentResource = new(
        "Ed-Fi",
        "StudentAssessment"
    );

    // ── Direct basis, live ────────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_direct_basis_filters_deleted_associations_by_the_live_school_and_composes_as_AND()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long regularSchoolDocumentId = await InsertSchoolRootAsync(
            RegularSchoolId,
            "Regular High",
            RegularSchoolTypeUri
        );
        long studentADocumentId = await InsertStudentAsync("STU-CV-Direct-A");
        long studentBDocumentId = await InsertStudentAsync("STU-CV-Direct-B");
        long alternativeAssociationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            studentADocumentId,
            "STU-CV-Direct-A"
        );
        long regularAssociationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            regularSchoolDocumentId,
            RegularSchoolId,
            studentBDocumentId,
            "STU-CV-Direct-B"
        );
        await CreateCustomAuthViewAsync(
            SchoolWithAlternativeTypeStrategy,
            SchoolWithTypeViewSql("Alternative")
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentSchoolAssociation", alternativeAssociationDocumentId);
        await DeleteEdfiRootAsync("StudentSchoolAssociation", regularAssociationDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // The live seek finds S1 through c.OldSchoolId_Unified and S1 is in the view; S2 is not.
        TrackedChangeQueryResult customViewOnly = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            [SchoolWithAlternativeTypeStrategy],
            claimEdOrgIds: [],
            window
        );
        customViewOnly.AuthorizationFailure.Should().BeNull();
        customViewOnly.TotalCount.Should().Be(1);
        customViewOnly.Items.Should().ContainSingle();
        customViewOnly.Items[0]!["keyValues"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be("STU-CV-Direct-A");

        // The relationship strategy alone authorizes S2's tombstone for a claim on S2...
        TrackedChangeQueryResult relationshipOnly = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            [AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly],
            claimEdOrgIds: [RegularSchoolId],
            window
        );
        relationshipOnly.Items.Should().ContainSingle();
        relationshipOnly.Items[0]!["keyValues"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be("STU-CV-Direct-B");

        // ...but composed with the custom view the two filters AND together and the intersection is empty.
        TrackedChangeQueryResult composed = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            [
                SchoolWithAlternativeTypeStrategy,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            ],
            claimEdOrgIds: [RegularSchoolId],
            window
        );
        composed.AuthorizationFailure.Should().BeNull();
        composed.TotalCount.Should().Be(0);
        composed.Items.Should().BeEmpty();
    }

    // ── Deleted basis, no suffix ──────────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_denies_a_tombstone_whose_basis_school_was_deleted_without_the_suffix()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-DeletedBasis");
        long associationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            studentDocumentId,
            "STU-CV-DeletedBasis"
        );
        await CreateCustomAuthViewAsync(
            SchoolWithAlternativeTypeStrategy,
            SchoolWithTypeViewSql("Alternative")
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentSchoolAssociation", associationDocumentId);
        ChangeVersionRange beforeSchoolDelete = new(windowStart, await GetNewestChangeVersionAsync());

        // While the school is live, the seek finds it and the association tombstone is authorized.
        TrackedChangeQueryResult whileSchoolLive = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            [SchoolWithAlternativeTypeStrategy],
            claimEdOrgIds: [],
            beforeSchoolDelete
        );
        whileSchoolLive.TotalCount.Should().Be(1);
        whileSchoolLive.Items.Should().ContainSingle();

        await DeleteEdfiRootAsync("School", alternativeSchoolDocumentId);
        ChangeVersionRange afterSchoolDelete = new(windowStart, await GetNewestChangeVersionAsync());

        // Once the school is gone the live seek finds nothing: without the IncludingDeletes suffix the
        // tombstone is denied, matching ODS behavior for a view over live tables.
        TrackedChangeQueryResult afterDelete = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            [SchoolWithAlternativeTypeStrategy],
            claimEdOrgIds: [],
            afterSchoolDelete
        );
        afterDelete.AuthorizationFailure.Should().BeNull();
        afterDelete.TotalCount.Should().Be(0);
        afterDelete.Items.Should().BeEmpty();
    }

    // ── Deleted basis, suffixed view ──────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_with_IncludingDeletes_suffix_authorizes_a_tombstone_through_the_basis_tombstone()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-Suffixed");
        long associationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            studentDocumentId,
            "STU-CV-Suffixed"
        );
        // The live arm is the same rule as the unsuffixed view. The School tombstone carries no descriptor
        // column, so the tombstone arm returns every deleted school (the implementer's choice per the
        // migration note); DMS's probe arm is separate and reads tracked_changes_edfi.School directly.
        await CreateCustomAuthViewAsync(
            "SchoolWithAlternativeTypeIncludingDeletes",
            SchoolWithTypeViewSql("Alternative")
                + """

                UNION
                SELECT tombstone.[DocumentId]
                FROM [tracked_changes_edfi].[School] tombstone
                """
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentSchoolAssociation", associationDocumentId);
        await DeleteEdfiRootAsync("School", alternativeSchoolDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        long trackedSchoolDocumentId = await _database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [tracked_changes_edfi].[School]
            WHERE [OldSchoolId] = @schoolId;
            """,
            new SqlParameter("@schoolId", AlternativeSchoolId)
        );
        trackedSchoolDocumentId.Should().Be(alternativeSchoolDocumentId);

        TrackedChangeQueryResult result = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            ["SchoolWithAlternativeTypeIncludingDeletes"],
            claimEdOrgIds: [],
            window
        );

        result.AuthorizationFailure.Should().BeNull();
        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["studentUniqueId"]!.GetValue<string>().Should().Be("STU-CV-Suffixed");
    }

    // ── Self basis on /keyChanges and /deletes ────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_self_basis_authorizes_section_key_changes_and_deletes_through_the_document_id_column()
    {
        await SeedGradeChainReferenceDataAsync();
        SectionChainSeed chain = await InsertSectionChainAsync();
        long sectionX1DocumentId = chain.SectionDocumentId;
        long sectionX2DocumentId = await InsertSectionRootAsync(
            chain.ChainSchoolId,
            chain.CourseOfferingDocumentId,
            chain.LocalCourseCode,
            chain.SessionName,
            "SEEK-SEC-2"
        );
        // A self-basis view over the subject's own root table; it returns X1 only.
        await CreateCustomAuthViewAsync(
            "SectionWithEvenIdentifier",
            $"""
            SELECT section.[DocumentId]
            FROM [edfi].[Section] section
            WHERE section.[DocumentId] = {sectionX1DocumentId}
            """
        );

        long keyChangeWindowStart = await GetNewestChangeVersionAsync() + 1;
        await UpdateSectionIdentifierAsync(sectionX1DocumentId, "SEEK-SEC-1-Renamed");
        await UpdateSectionIdentifierAsync(sectionX2DocumentId, "SEEK-SEC-2-Renamed");
        ChangeVersionRange keyChangeWindow = new(keyChangeWindowStart, await GetNewestChangeVersionAsync());

        TrackedChangeQueryResult keyChanges = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            SectionResource,
            ["SectionWithEvenIdentifier"],
            claimEdOrgIds: [],
            keyChangeWindow
        );
        keyChanges.AuthorizationFailure.Should().BeNull();
        keyChanges.TotalCount.Should().Be(1);
        keyChanges.Items.Should().ContainSingle();
        keyChanges.Items[0]!["oldKeyValues"]!["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be(chain.SectionIdentifier);
        keyChanges.Items[0]!["newKeyValues"]!["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("SEEK-SEC-1-Renamed");

        long deleteWindowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("Section", sectionX1DocumentId);
        ChangeVersionRange deleteWindow = new(deleteWindowStart, await GetNewestChangeVersionAsync());

        // The self basis reads the DocumentId system column, so the SQL is the same with or without the
        // suffix; only the view's contents decide. The live-only view no longer returns the deleted X1.
        TrackedChangeQueryResult deletesLiveOnly = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            SectionResource,
            ["SectionWithEvenIdentifier"],
            claimEdOrgIds: [],
            deleteWindow
        );
        deletesLiveOnly.AuthorizationFailure.Should().BeNull();
        deletesLiveOnly.TotalCount.Should().Be(0);
        deletesLiveOnly.Items.Should().BeEmpty();

        await CreateCustomAuthViewAsync(
            "SectionWithEvenIdentifierIncludingDeletes",
            $"""
            SELECT section.[DocumentId]
            FROM [edfi].[Section] section
            WHERE section.[DocumentId] = {sectionX1DocumentId}
            UNION
            SELECT tombstone.[DocumentId]
            FROM [tracked_changes_edfi].[Section] tombstone
            """
        );

        TrackedChangeQueryResult deletesIncludingDeleted = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            SectionResource,
            ["SectionWithEvenIdentifierIncludingDeletes"],
            claimEdOrgIds: [],
            deleteWindow
        );
        deletesIncludingDeleted.AuthorizationFailure.Should().BeNull();
        deletesIncludingDeleted.TotalCount.Should().Be(1);
        deletesIncludingDeleted.Items.Should().ContainSingle();
        deletesIncludingDeleted.Items[0]!["keyValues"]!["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("SEEK-SEC-1-Renamed");
    }

    [Test]
    public async Task ReadChanges_custom_view_self_basis_authorizes_a_key_change_into_the_view_by_current_membership()
    {
        await SeedGradeChainReferenceDataAsync();
        SectionChainSeed chain = await InsertSectionChainAsync();
        // The view accepts one identifier the seeded Section (SEEK-SEC-1) does not have yet.
        await CreateCustomAuthViewAsync(
            "SectionWithReviewedIdentifier",
            """
            SELECT section.[DocumentId]
            FROM [edfi].[Section] section
            WHERE section.[SectionIdentifier] = 'REVIEWED'
            """
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await UpdateSectionIdentifierAsync(chain.SectionDocumentId, "REVIEWED");
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // A stored-DocumentId basis authorizes by the document's current view membership, the rule ODS
        // applies to its surrogate-keyed bases (c.OldStudentUSI = view.StudentUSI survives a unique-id
        // change). The renamed Section is in the view now, so its key change is returned, old key included.
        TrackedChangeQueryResult keyChanges = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            SectionResource,
            ["SectionWithReviewedIdentifier"],
            claimEdOrgIds: [],
            window
        );
        keyChanges.AuthorizationFailure.Should().BeNull();
        keyChanges.TotalCount.Should().Be(1);
        keyChanges.Items.Should().ContainSingle();
        keyChanges.Items[0]!["oldKeyValues"]!["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be(chain.SectionIdentifier);
        keyChanges.Items[0]!["newKeyValues"]!["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("REVIEWED");

        // Renamed back out of the view, the next key change is denied: membership is current, both ways.
        long secondWindowStart = await GetNewestChangeVersionAsync() + 1;
        await UpdateSectionIdentifierAsync(chain.SectionDocumentId, chain.SectionIdentifier);
        ChangeVersionRange secondWindow = new(secondWindowStart, await GetNewestChangeVersionAsync());

        TrackedChangeQueryResult afterRenameBack = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            SectionResource,
            ["SectionWithReviewedIdentifier"],
            claimEdOrgIds: [],
            secondWindow
        );
        afterRenameBack.AuthorizationFailure.Should().BeNull();
        afterRenameBack.TotalCount.Should().Be(0);
        afterRenameBack.Items.Should().BeEmpty();
    }

    [Test]
    public async Task ReadChanges_custom_view_with_IncludingDeletes_suffix_resolves_a_renamed_basis_through_its_key_change_row()
    {
        await SeedGradeChainReferenceDataAsync();
        SectionChainSeed chain = await InsertSectionChainAsync();
        long studentDocumentId = await InsertStudentAsync("STU-CV-Renamed");
        long associationDocumentId = await InsertStudentSectionAssociationRootAsync(
            chain,
            studentDocumentId,
            "STU-CV-Renamed"
        );
        long gradeDocumentId = await InsertGradeRootAsync(chain, associationDocumentId, "STU-CV-Renamed");
        // Both arms accept the identifier the Section will be renamed to; neither accepts SEEK-SEC-1.
        await CreateCustomAuthViewAsync(
            "SectionWithReviewedIdentifierIncludingDeletes",
            """
            SELECT section.[DocumentId]
            FROM [edfi].[Section] section
            WHERE section.[SectionIdentifier] = 'REVIEWED'
            UNION
            SELECT tombstone.[DocumentId]
            FROM [tracked_changes_edfi].[Section] tombstone
            WHERE tombstone.[OldSectionIdentifier] = 'REVIEWED'
            """
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("Grade", gradeDocumentId);
        await DeleteEdfiRootAsync("StudentSectionAssociation", associationDocumentId);
        await UpdateSectionIdentifierAsync(chain.SectionDocumentId, "REVIEWED");
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // Without the suffix the live seek by the Grade tombstone's old Section key (SEEK-SEC-1) finds no
        // row, so the renamed basis is denied, as in ODS for a natural-key basis.
        await CreateCustomAuthViewAsync(
            "SectionWithReviewedIdentifier",
            """
            SELECT section.[DocumentId]
            FROM [edfi].[Section] section
            WHERE section.[SectionIdentifier] = 'REVIEWED'
            """
        );
        TrackedChangeQueryResult liveOnly = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            GradeResource,
            ["SectionWithReviewedIdentifier"],
            claimEdOrgIds: [],
            window
        );
        liveOnly.AuthorizationFailure.Should().BeNull();
        liveOnly.TotalCount.Should().Be(0);
        liveOnly.Items.Should().BeEmpty();

        // With the suffix the probe matches the Section's key-change row by the old key and resolves the
        // basis to its DocumentId, which the view's live arm holds under the new identifier. The suffix
        // resolves a renamed basis as well as a deleted one; membership is then by the document, not by
        // the historical key the tombstone recorded.
        TrackedChangeQueryResult withSuffix = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            GradeResource,
            ["SectionWithReviewedIdentifierIncludingDeletes"],
            claimEdOrgIds: [],
            window
        );
        withSuffix.AuthorizationFailure.Should().BeNull();
        withSuffix.TotalCount.Should().Be(1);
        withSuffix.Items.Should().ContainSingle();
        withSuffix.Items[0]!["keyValues"]!["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be(chain.SectionIdentifier);
    }

    // ── Transitive basis ──────────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_transitive_basis_filters_deleted_grades_by_course_offering()
    {
        await SeedGradeChainReferenceDataAsync();
        await SeedSchoolTypeDescriptorsAsync();
        long otherSchoolDocumentId = await InsertSchoolRootAsync(
            RegularSchoolId,
            "Regular High",
            RegularSchoolTypeUri
        );
        SectionChainSeed fixtureSchoolChain = await InsertSectionChainAsync();
        SectionChainSeed otherSchoolChain = await InsertSectionChainAsync(
            otherSchoolDocumentId,
            RegularSchoolId,
            suffix: "-B"
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-Grade");
        long fixtureAssociationDocumentId = await InsertStudentSectionAssociationRootAsync(
            fixtureSchoolChain,
            studentDocumentId,
            "STU-CV-Grade"
        );
        long otherAssociationDocumentId = await InsertStudentSectionAssociationRootAsync(
            otherSchoolChain,
            studentDocumentId,
            "STU-CV-Grade"
        );
        long fixtureGradeDocumentId = await InsertGradeRootAsync(
            fixtureSchoolChain,
            fixtureAssociationDocumentId,
            "STU-CV-Grade"
        );
        long otherGradeDocumentId = await InsertGradeRootAsync(
            otherSchoolChain,
            otherAssociationDocumentId,
            "STU-CV-Grade"
        );
        // Course offerings of the fixture school only. Grade reaches CourseOffering through
        // StudentSectionAssociation and Section; the seek pairs the offering's four identity parts with
        // the Grade tombstone's Old* columns.
        await CreateCustomAuthViewAsync(
            "CourseOfferingWithChangeQuerySchool",
            $"""
            SELECT courseOffering.[DocumentId]
            FROM [edfi].[CourseOffering] courseOffering
            WHERE courseOffering.[SchoolId_Unified] = {SchoolId}
            """
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("Grade", fixtureGradeDocumentId);
        await DeleteEdfiRootAsync("Grade", otherGradeDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        TrackedChangeQueryResult result = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            GradeResource,
            ["CourseOfferingWithChangeQuerySchool"],
            claimEdOrgIds: [],
            window
        );

        result.AuthorizationFailure.Should().BeNull();
        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["localCourseCode"]!
            .GetValue<string>()
            .Should()
            .Be(fixtureSchoolChain.LocalCourseCode);
    }

    // ── Person basis ──────────────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_person_basis_filters_deleted_associations_by_student_and_composes_with_people_strategy()
    {
        await SeedGradeChainReferenceDataAsync();
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        SectionChainSeed cteChain = await InsertSectionChainAsync();
        long enrolledStudentDocumentId = await InsertStudentAsync("STU-CV-CTE-Enrolled");
        long otherStudentDocumentId = await InsertStudentAsync("STU-CV-CTE-Other");
        await InsertStudentSectionAssociationRootAsync(
            cteChain,
            enrolledStudentDocumentId,
            "STU-CV-CTE-Enrolled"
        );
        long enrolledAssociationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            enrolledStudentDocumentId,
            "STU-CV-CTE-Enrolled"
        );
        long otherAssociationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            otherStudentDocumentId,
            "STU-CV-CTE-Other"
        );
        // auth.md's StudentWithCTECourseEnrollments adapted to DocumentId: students with a section
        // enrollment in the "CTE" course offering (the fixture chain's local course code stands in for the
        // academic-subject filter).
        await CreateCustomAuthViewAsync(
            "StudentWithCTECourseEnrollments",
            $"""
            SELECT association.[Student_DocumentId] AS [DocumentId]
            FROM [edfi].[StudentSectionAssociation] association
            INNER JOIN [edfi].[Section] section ON section.[DocumentId] = association.[Section_DocumentId]
            INNER JOIN [edfi].[CourseOffering] courseOffering
                ON courseOffering.[DocumentId] = section.[CourseOffering_DocumentId]
            WHERE courseOffering.[LocalCourseCode] = '{cteChain.LocalCourseCode}'
            """
        );
        // Both students are enrolled at the alternative school for the IncludingDeletes people view.
        await InsertAuthEdOrgTupleAsync(AlternativeSchoolId, AlternativeSchoolId);
        await InsertAuthEdOrgTupleAsync(RegularSchoolId, RegularSchoolId);

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentSchoolAssociation", enrolledAssociationDocumentId);
        await DeleteEdfiRootAsync("StudentSchoolAssociation", otherAssociationDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        TrackedChangeQueryResult customViewOnly = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            ["StudentWithCTECourseEnrollments"],
            claimEdOrgIds: [],
            window
        );
        customViewOnly.AuthorizationFailure.Should().BeNull();
        customViewOnly.TotalCount.Should().Be(1);
        customViewOnly.Items.Should().ContainSingle();
        customViewOnly.Items[0]!["keyValues"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be("STU-CV-CTE-Enrolled");

        // The people strategy alone authorizes both tombstones for the alternative school's claim (both
        // students' deleted associations feed the IncludingDeletes view); AND-composed with the custom
        // view only the enrolled student survives, and a claim on the other school authorizes nothing.
        TrackedChangeQueryResult peopleOnly = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            ["RelationshipsWithStudentsOnlyIncludingDeletes"],
            claimEdOrgIds: [AlternativeSchoolId],
            window
        );
        peopleOnly.TotalCount.Should().Be(2);

        TrackedChangeQueryResult composedAuthorizedClaim = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            ["StudentWithCTECourseEnrollments", "RelationshipsWithStudentsOnlyIncludingDeletes"],
            claimEdOrgIds: [AlternativeSchoolId],
            window
        );
        composedAuthorizedClaim.TotalCount.Should().Be(1);
        composedAuthorizedClaim.Items.Should().ContainSingle();
        composedAuthorizedClaim.Items[0]!["keyValues"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be("STU-CV-CTE-Enrolled");

        TrackedChangeQueryResult composedOtherClaim = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            ["StudentWithCTECourseEnrollments", "RelationshipsWithStudentsOnlyIncludingDeletes"],
            claimEdOrgIds: [RegularSchoolId],
            window
        );
        composedOtherClaim.TotalCount.Should().Be(0);
        composedOtherClaim.Items.Should().BeEmpty();
    }

    // ── Securable non-identity first hop ──────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_securable_non_identity_first_hop_filters_deleted_student_assessments_by_reported_school()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long regularSchoolDocumentId = await InsertSchoolRootAsync(
            RegularSchoolId,
            "Regular High",
            RegularSchoolTypeUri
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-Assessed");
        const string assessmentIdentifier = "CV-ASSESSMENT";
        const string assessmentNamespace = "uri://ed-fi.org/Assessment";
        long assessmentDocumentId = await InsertAssessmentRootAsync(
            assessmentIdentifier,
            assessmentNamespace
        );
        long reportedAlternativeDocumentId = await InsertStudentAssessmentRootAsync(
            assessmentDocumentId,
            assessmentIdentifier,
            assessmentNamespace,
            studentDocumentId,
            "STU-CV-Assessed",
            "SA-Alternative",
            alternativeSchoolDocumentId,
            AlternativeSchoolId
        );
        long reportedRegularDocumentId = await InsertStudentAssessmentRootAsync(
            assessmentDocumentId,
            assessmentIdentifier,
            assessmentNamespace,
            studentDocumentId,
            "STU-CV-Assessed",
            "SA-Regular",
            regularSchoolDocumentId,
            RegularSchoolId
        );
        long unreportedDocumentId = await InsertStudentAssessmentRootAsync(
            assessmentDocumentId,
            assessmentIdentifier,
            assessmentNamespace,
            studentDocumentId,
            "STU-CV-Assessed",
            "SA-Unreported",
            reportedSchoolDocumentId: null,
            reportedSchoolId: null
        );
        await CreateCustomAuthViewAsync(
            SchoolWithAlternativeTypeStrategy,
            SchoolWithTypeViewSql("Alternative")
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentAssessment", reportedAlternativeDocumentId);
        await DeleteEdfiRootAsync("StudentAssessment", reportedRegularDocumentId);
        await DeleteEdfiRootAsync("StudentAssessment", unreportedDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // reportedSchoolReference is a securable element but not part of the identity; the tombstone
        // stores OldReportedSchool_SchoolId, so the seek works, and a null old value never matches.
        TrackedChangeQueryResult result = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentAssessmentResource,
            [SchoolWithAlternativeTypeStrategy],
            claimEdOrgIds: [],
            window
        );

        result.AuthorizationFailure.Should().BeNull();
        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0]!["keyValues"]!["studentAssessmentIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be("SA-Alternative");
    }

    // ── Errors ────────────────────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_over_a_non_identifying_non_securable_reference_fails_planning_while_live_reads_succeed()
    {
        await SeedGradeChainReferenceDataAsync();
        SectionChainSeed chain = await InsertSectionChainAsync();
        await CreateCustomAuthViewAsync(
            "LocationWithX",
            """
            SELECT location.[DocumentId]
            FROM [edfi].[Location] location
            """
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("Section", chain.SectionDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // Section's location reference is optional and neither identifying nor securable, so the Section
        // tombstone carries no Location values to seek by: planning fails with the ODS-parity hint.
        TrackedChangeQueryResult deletes = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            SectionResource,
            ["LocationWithX"],
            claimEdOrgIds: [],
            window
        );

        ChangeQueryAuthorizationFailure.SecurityConfiguration failure = deletes
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().BeEmpty();
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("'LocationWithX'");
        failure.Errors[0].Should().Contain("'Ed-Fi.Location'");
        failure.Errors[0].Should().Contain(CustomViewBasisNotIdentifyingOrSecurableHintFragment);
        deletes.TotalCount.Should().BeNull();
        deletes.Items.Should().BeEmpty();

        // The same view keeps working on the live read path, as in ODS.
        QueryResult liveQuery = await QueryLiveDocumentsAsync(SectionResource, ["LocationWithX"]);
        liveQuery.Should().BeOfType<QueryResult.QuerySuccess>();
    }

    [Test]
    public async Task ReadChanges_custom_view_whose_view_is_missing_fails_validation_before_reading_rows()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-MissingView");
        long associationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            studentDocumentId,
            "STU-CV-MissingView"
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentSchoolAssociation", associationDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // The per-request validator runs before the change query: a missing view surfaces as the
        // CustomViewAuthorizationValidationException that the middleware maps to the urn:ed-fi:api:system
        // 500, and no rows are returned.
        Func<Task> act = () =>
            QueryCustomViewChangesAsync(
                ChangeQueryEndpointOperation.Deletes,
                StudentSchoolAssociationResource,
                ["SchoolWithMissingChangeQueryView"],
                claimEdOrgIds: [],
                window
            );

        var assertion = await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        SqlException providerException = assertion
            .Which.InnerException.Should()
            .BeOfType<SqlException>()
            .Subject;
        providerException.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task ReadChanges_custom_view_with_an_unknown_basis_returns_the_unknown_strategy_failure()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-UnknownBasis");
        long associationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            studentDocumentId,
            "STU-CV-UnknownBasis"
        );

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await DeleteEdfiRootAsync("StudentSchoolAssociation", associationDocumentId);
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        TrackedChangeQueryResult result = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.Deletes,
            StudentSchoolAssociationResource,
            ["FooWithBar"],
            claimEdOrgIds: [],
            window
        );

        ChangeQueryAuthorizationFailure.SecurityConfiguration failure = result
            .AuthorizationFailure.Should()
            .BeOfType<ChangeQueryAuthorizationFailure.SecurityConfiguration>()
            .Subject;
        failure.UnavailableStrategyNames.Should().Equal("FooWithBar");
        failure
            .Errors.Should()
            .Equal(SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies(["FooWithBar"]));
        result.TotalCount.Should().BeNull();
        result.Items.Should().BeEmpty();
    }

    // ── Old values only ───────────────────────────────────────────────────

    [Test]
    public async Task ReadChanges_custom_view_authorizes_key_changes_by_the_old_school_only()
    {
        await SeedSchoolTypeDescriptorsAsync();
        long alternativeSchoolDocumentId = await InsertSchoolRootAsync(
            AlternativeSchoolId,
            "Alternative High",
            AlternativeSchoolTypeUri
        );
        long regularSchoolDocumentId = await InsertSchoolRootAsync(
            RegularSchoolId,
            "Regular High",
            RegularSchoolTypeUri
        );
        long studentDocumentId = await InsertStudentAsync("STU-CV-Moved");
        long associationDocumentId = await InsertStudentSchoolAssociationRootAsync(
            alternativeSchoolDocumentId,
            AlternativeSchoolId,
            studentDocumentId,
            "STU-CV-Moved"
        );
        await CreateCustomAuthViewAsync(
            SchoolWithAlternativeTypeStrategy,
            SchoolWithTypeViewSql("Alternative")
        );
        await CreateCustomAuthViewAsync("SchoolWithRegularType", SchoolWithTypeViewSql("Regular"));

        long windowStart = await GetNewestChangeVersionAsync() + 1;
        await MoveStudentSchoolAssociationToSchoolAsync(
            associationDocumentId,
            regularSchoolDocumentId,
            RegularSchoolId
        );
        ChangeVersionRange window = new(windowStart, await GetNewestChangeVersionAsync());

        // Key changes authorize by the OLD values: the association moved from the alternative school
        // (S1) to the regular school (S2), so the alternative-type view returns the key change...
        TrackedChangeQueryResult byOldSchool = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            StudentSchoolAssociationResource,
            [SchoolWithAlternativeTypeStrategy],
            claimEdOrgIds: [],
            window
        );
        byOldSchool.AuthorizationFailure.Should().BeNull();
        byOldSchool.TotalCount.Should().Be(1);
        byOldSchool.Items.Should().ContainSingle();
        JsonObject keyChange = byOldSchool.Items[0]!.AsObject();
        keyChange["oldKeyValues"]!["schoolId"]!.GetValue<long>().Should().Be(AlternativeSchoolId);
        keyChange["newKeyValues"]!["schoolId"]!.GetValue<long>().Should().Be(RegularSchoolId);

        // ...and the regular-type view does not, even though the NEW school is regular.
        TrackedChangeQueryResult byNewSchool = await QueryCustomViewChangesAsync(
            ChangeQueryEndpointOperation.KeyChanges,
            StudentSchoolAssociationResource,
            ["SchoolWithRegularType"],
            claimEdOrgIds: [],
            window
        );
        byNewSchool.AuthorizationFailure.Should().BeNull();
        byNewSchool.TotalCount.Should().Be(0);
        byNewSchool.Items.Should().BeEmpty();
    }

    // ── Custom view query helpers ─────────────────────────────────────────

    private RelationalChangeQueryRepository CreateChangeQueryRepository()
    {
        var commandExecutor = new MssqlRelationalCommandExecutor(
            async ct =>
            {
                var connection = new SqlConnection(_database.ConnectionString);
                await connection.OpenAsync(ct);
                return connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );
        return new RelationalChangeQueryRepository(
            commandExecutor,
            new MssqlRelationalParameterConfigurator()
        );
    }

    private async Task<long> GetNewestChangeVersionAsync() =>
        await CreateChangeQueryRepository().GetNewestChangeVersion();

    private Task<TrackedChangeQueryResult> QueryCustomViewChangesAsync(
        ChangeQueryEndpointOperation operation,
        QualifiedResourceName resource,
        IReadOnlyList<string> strategies,
        IReadOnlyList<long> claimEdOrgIds,
        ChangeVersionRange changeVersionRange
    )
    {
        ConcreteResourceModel resourceModel = ResolveResourceModel(resource);
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(x =>
            x.SourceTable == resourceModel.RelationalModel.Root.Table
        );

        return QueryTrackedChangesWithAuthorizationAsync(
            operation,
            ResolveResourceInfo(resource),
            resourceModel,
            trackedTable,
            claimEdOrgIds,
            namespacePrefixes: [],
            strategies,
            limit: 25,
            offset: 0,
            totalCount: true,
            changeVersionRange,
            traceId: new TraceId($"mssql-readchanges-customview-{operation}-{resource.ResourceName}")
        );
    }

    private async Task<QueryResult> QueryLiveDocumentsAsync(
        QualifiedResourceName resource,
        IReadOnlyList<string> strategies
    )
    {
        ResourceInfo resourceInfo = ResolveResourceInfo(resource);
        return await InvokeDocumentStoreAsync(repository =>
            repository.QueryDocuments(
                new RelationalQueryRequest(
                    ResourceInfo: resourceInfo,
                    AuthorizationContext: new RelationalAuthorizationContext([], []),
                    MappingSet: _mappingSet,
                    QueryElements: [],
                    AuthorizationStrategyEvaluators:
                    [
                        .. strategies.Select(static name => new AuthorizationStrategyEvaluator(
                            name,
                            [],
                            FilterOperator.And
                        )),
                    ],
                    Paging: new CollectionPaging.Traditional(
                        new PaginationParameters(
                            Limit: 25,
                            Offset: 0,
                            TotalCount: true,
                            MaximumPageSize: MaximumPageSize
                        )
                    ),
                    TraceId: new TraceId($"mssql-live-query-{resource.ResourceName}"),
                    PageOrderingMode: PageOrderingMode.DocumentId
                )
            )
        );
    }

    // ── Custom view seed helpers ──────────────────────────────────────────

    /// <summary>
    /// Drops and recreates [auth].[{strategyName}] from the given SELECT. Views survive the per-test
    /// table reset, so each scenario (re)creates the views it depends on. CREATE VIEW must be the only
    /// statement in its batch, hence the two commands.
    /// </summary>
    private async Task CreateCustomAuthViewAsync(string strategyName, string selectSql)
    {
        await _database.ExecuteNonQueryAsync($"DROP VIEW IF EXISTS [auth].[{strategyName}];");
        await _database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW [auth].[{strategyName}] AS
            {selectSql};
            """
        );
    }

    /// <summary>The auth.md example view: schools whose SchoolTypeDescriptor has the given code value.</summary>
    private static string SchoolWithTypeViewSql(string schoolTypeCodeValue) =>
        $"""
            SELECT school.[DocumentId]
            FROM [edfi].[School] school
            INNER JOIN [dms].[Descriptor] descriptor
                ON descriptor.[DocumentId] = school.[SchoolTypeDescriptor_DescriptorId]
            WHERE descriptor.[Namespace] = '{SchoolTypeDescriptorNamespace}'
              AND descriptor.[CodeValue] = '{schoolTypeCodeValue}'
            """;

    private async Task SeedSchoolTypeDescriptorsAsync()
    {
        await SeedDescriptorAsync(
            Guid.NewGuid(),
            "SchoolTypeDescriptor",
            "Ed-Fi:SchoolTypeDescriptor",
            AlternativeSchoolTypeUri,
            SchoolTypeDescriptorNamespace,
            "Alternative",
            "Alternative"
        );
        await SeedDescriptorAsync(
            Guid.NewGuid(),
            "SchoolTypeDescriptor",
            "Ed-Fi:SchoolTypeDescriptor",
            RegularSchoolTypeUri,
            SchoolTypeDescriptorNamespace,
            "Regular",
            "Regular"
        );
    }

    private async Task<long> InsertSchoolRootAsync(
        long schoolId,
        string nameOfInstitution,
        string schoolTypeDescriptorUri
    )
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "School")
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId],
                [SchoolTypeDescriptor_DescriptorId])
            VALUES (@documentId, @nameOfInstitution, @schoolId,
                {DescriptorDocumentIdSql("SchoolTypeDescriptor", "@schoolTypeDescriptorUri")});
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@schoolTypeDescriptorUri", schoolTypeDescriptorUri)
        );
        return documentId;
    }

    private async Task<long> InsertStudentAsync(string studentUniqueId)
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "Student")
        );
        await InsertStudentRootAsync(documentId, studentUniqueId);
        return documentId;
    }

    private async Task<long> InsertStudentSchoolAssociationRootAsync(
        long schoolDocumentId,
        long schoolId,
        long studentDocumentId,
        string studentUniqueId
    )
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "StudentSchoolAssociation")
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            INSERT INTO [edfi].[StudentSchoolAssociation] ([DocumentId], [SchoolId_Unified], [School_DocumentId],
                [Student_DocumentId], [Student_StudentUniqueId], [EntryGradeLevelDescriptor_DescriptorId], [EntryDate])
            VALUES (@documentId, @schoolId, @schoolDocumentId, @studentDocumentId, @studentUniqueId,
                {DescriptorDocumentIdSql(
                "GradeLevelDescriptor",
                "@entryGradeLevelDescriptorUri"
            )}, @entryDate);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@entryGradeLevelDescriptorUri", EntryGradeLevelDescriptorUri),
            new SqlParameter("@entryDate", CustomViewEntryDate)
        );
        return documentId;
    }

    /// <summary>
    /// Moves the association to another school the way an identity update lands in the root table: the
    /// reference DocumentId and the unified school id change together, firing the key-change trigger.
    /// </summary>
    private async Task MoveStudentSchoolAssociationToSchoolAsync(
        long associationDocumentId,
        long newSchoolDocumentId,
        long newSchoolId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StudentSchoolAssociation]
            SET [School_DocumentId] = @schoolDocumentId, [SchoolId_Unified] = @schoolId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@schoolDocumentId", newSchoolDocumentId),
            new SqlParameter("@schoolId", newSchoolId),
            new SqlParameter("@documentId", associationDocumentId)
        );
    }

    private async Task UpdateSectionIdentifierAsync(long sectionDocumentId, string sectionIdentifier)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Section]
            SET [SectionIdentifier] = @sectionIdentifier
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@sectionIdentifier", sectionIdentifier),
            new SqlParameter("@documentId", sectionDocumentId)
        );
    }

    private async Task<long> InsertAssessmentRootAsync(string assessmentIdentifier, string @namespace)
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "Assessment")
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Assessment] ([DocumentId], [AssessmentIdentifier], [AssessmentTitle], [Namespace])
            VALUES (@documentId, @assessmentIdentifier, 'Custom view assessment', @namespace);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@assessmentIdentifier", assessmentIdentifier),
            new SqlParameter("@namespace", @namespace)
        );
        return documentId;
    }

    private async Task<long> InsertStudentAssessmentRootAsync(
        long assessmentDocumentId,
        string assessmentIdentifier,
        string @namespace,
        long studentDocumentId,
        string studentUniqueId,
        string studentAssessmentIdentifier,
        long? reportedSchoolDocumentId,
        long? reportedSchoolId
    )
    {
        long documentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            await GetResourceKeyIdAsync("Ed-Fi", "StudentAssessment")
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[StudentAssessment] ([DocumentId], [Assessment_DocumentId],
                [Assessment_AssessmentIdentifier], [Assessment_Namespace], [ReportedSchool_DocumentId],
                [ReportedSchool_SchoolId], [Student_DocumentId], [Student_StudentUniqueId],
                [StudentAssessmentIdentifier])
            VALUES (@documentId, @assessmentDocumentId, @assessmentIdentifier, @namespace,
                @reportedSchoolDocumentId, @reportedSchoolId, @studentDocumentId, @studentUniqueId,
                @studentAssessmentIdentifier);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@assessmentDocumentId", assessmentDocumentId),
            new SqlParameter("@assessmentIdentifier", assessmentIdentifier),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@reportedSchoolDocumentId", SqlDbType.BigInt)
            {
                Value = (object?)reportedSchoolDocumentId ?? DBNull.Value,
            },
            new SqlParameter("@reportedSchoolId", SqlDbType.BigInt)
            {
                Value = (object?)reportedSchoolId ?? DBNull.Value,
            },
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@studentAssessmentIdentifier", studentAssessmentIdentifier)
        );
        return documentId;
    }

    /// <summary>Deletes one root row through the table, firing the resource's tombstone trigger.</summary>
    private async Task DeleteEdfiRootAsync(string tableName, long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            $"""
            DELETE FROM [edfi].[{tableName}]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }
}
