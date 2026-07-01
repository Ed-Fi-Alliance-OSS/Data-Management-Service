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

        result.TotalCount.Should().Be(1L);
        result.Items.Should().ContainSingle();
        JsonObject item = result.Items[0]!.AsObject();
        item["id"]!.GetValue<string>().Should().Be(AcademicWeekDocumentUuid.Value.ToString("D"));
        item["changeVersion"]!.GetValue<long>().Should().Be(latestChangeVersion);
        item["changeVersion"]!.GetValue<long>().Should().BeGreaterThan(firstChangeVersion);

        JsonObject oldKeyValues = item["oldKeyValues"]!.AsObject();
        oldKeyValues["weekIdentifier"]!.GetValue<string>().Should().Be(WeekIdentifierA);
        oldKeyValues["schoolId"]!.GetValue<long>().Should().Be(SchoolId);

        JsonObject newKeyValues = item["newKeyValues"]!.AsObject();
        newKeyValues["weekIdentifier"]!.GetValue<string>().Should().Be(WeekIdentifierC);
        newKeyValues["schoolId"]!.GetValue<long>().Should().Be(SchoolId);
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
        services.AddMssqlReferenceResolver();

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
        var commandExecutor = new MssqlRelationalCommandExecutor(
            async ct =>
            {
                var connection = new SqlConnection(_database.ConnectionString);
                await connection.OpenAsync(ct);
                return connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );
        var repository = new RelationalChangeQueryRepository(
            commandExecutor,
            new MssqlRelationalParameterConfigurator()
        );
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
              AND [New_WeekIdentifier] IS NOT NULL;
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
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
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
        var commandExecutor = new MssqlRelationalCommandExecutor(
            async ct =>
            {
                var connection = new SqlConnection(_database.ConnectionString);
                await connection.OpenAsync(ct);
                return connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );
        var repository = new RelationalChangeQueryRepository(
            commandExecutor,
            new MssqlRelationalParameterConfigurator()
        );
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

    private async Task InsertAcademicWeekTombstoneAsync(string weekIdentifier, long oldSchoolId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[AcademicWeek]
                ([Old_School_SchoolId], [New_School_SchoolId], [Old_WeekIdentifier], [New_WeekIdentifier],
                 [Id], [ChangeVersion])
            VALUES (@oldSchoolId, NULL, @weekIdentifier, NULL, @id, @changeVersion);
            """,
            new SqlParameter("@oldSchoolId", oldSchoolId),
            new SqlParameter("@weekIdentifier", weekIdentifier),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
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
                ([Old_School_SchoolId], [New_School_SchoolId], [Old_WeekIdentifier], [New_WeekIdentifier],
                 [Id], [ChangeVersion])
            VALUES (@oldSchoolId, @newSchoolId, @oldWeekIdentifier, @newWeekIdentifier, @id, @changeVersion);
            """,
            new SqlParameter("@oldSchoolId", oldSchoolId),
            new SqlParameter("@newSchoolId", oldSchoolId),
            new SqlParameter("@oldWeekIdentifier", oldWeekIdentifier),
            new SqlParameter("@newWeekIdentifier", newWeekIdentifier),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
        );
    }

    private async Task InsertSurveyTombstoneAsync(string surveyIdentifier, string oldNamespace)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[Survey]
                ([Old_Namespace], [New_Namespace], [Old_SurveyIdentifier], [New_SurveyIdentifier],
                 [Id], [ChangeVersion])
            VALUES (@oldNamespace, NULL, @surveyIdentifier, NULL, @id, @changeVersion);
            """,
            new SqlParameter("@oldNamespace", oldNamespace),
            new SqlParameter("@surveyIdentifier", surveyIdentifier),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
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
                ([Old_Namespace], [New_Namespace], [Old_CodeValue], [New_CodeValue], [Discriminator],
                 [Id], [ChangeVersion])
            VALUES (@oldNamespace, NULL, @oldCodeValue, NULL, @discriminator, @id, @changeVersion);
            """,
            new SqlParameter("@oldNamespace", oldNamespace),
            new SqlParameter("@oldCodeValue", oldCodeValue),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
        );
    }

    private async Task InsertTrackedStudentSchoolAssociationAsync(long oldSchoolId, long oldStudentDocId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[StudentSchoolAssociation]
                ([Old_EntryDate], [New_EntryDate], [Old_SchoolId_Unified], [New_SchoolId_Unified],
                 [Old_Student_StudentUniqueId], [New_Student_StudentUniqueId],
                 [Old_Student_DocumentId], [New_Student_DocumentId], [Id], [ChangeVersion])
            VALUES (@entryDate, NULL, @oldSchoolId, NULL, @studentUniqueId, NULL, @oldStudentDocId, NULL,
                    @id, @changeVersion);
            """,
            new SqlParameter("@entryDate", new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@oldSchoolId", oldSchoolId),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
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
                ([Old_BeginDate], [New_BeginDate],
                 [Old_EducationOrganization_EducationOrganizationId], [New_EducationOrganization_EducationOrganizationId],
                 [Old_ResponsibilityDescriptor_Namespace], [New_ResponsibilityDescriptor_Namespace],
                 [Old_ResponsibilityDescriptor_CodeValue], [New_ResponsibilityDescriptor_CodeValue],
                 [Old_Student_StudentUniqueId], [New_Student_StudentUniqueId],
                 [Old_Student_DocumentId], [New_Student_DocumentId], [Id], [ChangeVersion])
            VALUES (@beginDate, NULL, @oldEdOrgId, NULL, @respNamespace, NULL, @respCodeValue, NULL,
                    @studentUniqueId, NULL, @oldStudentDocId, NULL, @id, @changeVersion);
            """,
            new SqlParameter("@beginDate", new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@oldEdOrgId", oldEdOrgId),
            new SqlParameter("@respNamespace", "uri://ed-fi.org/ResponsibilityDescriptor"),
            new SqlParameter("@respCodeValue", "Educational"),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
        );
    }

    private async Task InsertStudentHealthTombstoneAsync(long oldEdOrgId, long oldStudentDocId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [tracked_changes_edfi].[StudentHealth]
                ([Old_EducationOrganization_EducationOrganizationId], [New_EducationOrganization_EducationOrganizationId],
                 [Old_Student_StudentUniqueId], [New_Student_StudentUniqueId],
                 [Old_Student_DocumentId], [New_Student_DocumentId], [Id], [ChangeVersion])
            VALUES (@oldEdOrgId, NULL, @studentUniqueId, NULL, @oldStudentDocId, NULL, @id, @changeVersion);
            """,
            new SqlParameter("@oldEdOrgId", oldEdOrgId),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
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
                ([Old_DisciplineActionIdentifier], [New_DisciplineActionIdentifier],
                 [Old_DisciplineDate], [New_DisciplineDate],
                 [Old_Student_StudentUniqueId], [New_Student_StudentUniqueId],
                 [Old_ResponsibilitySchool_SchoolId], [New_ResponsibilitySchool_SchoolId],
                 [Old_Student_DocumentId], [New_Student_DocumentId], [Id], [ChangeVersion])
            VALUES (@identifier, NULL, @disciplineDate, NULL, @studentUniqueId, NULL, @schoolId, NULL,
                    @oldStudentDocId, NULL, @id, @changeVersion);
            """,
            new SqlParameter("@identifier", disciplineActionIdentifier),
            new SqlParameter("@disciplineDate", new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            new SqlParameter("@studentUniqueId", $"STU{oldStudentDocId}"),
            new SqlParameter("@schoolId", AuthClaimEdOrgId),
            new SqlParameter("@oldStudentDocId", oldStudentDocId),
            new SqlParameter("@id", Guid.NewGuid()),
            new SqlParameter("@changeVersion", NextAuthChangeVersion())
        );
    }
}
