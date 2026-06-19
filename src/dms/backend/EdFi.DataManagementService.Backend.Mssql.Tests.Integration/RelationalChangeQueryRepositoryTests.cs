// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
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
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Verifies RelationalChangeQueryRepository.GetNewestChangeVersion() reads dms.GetMaxChangeVersion()
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

        return new RelationalChangeQueryRepository(commandExecutor);
    }
}

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
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
[Category(MssqlCiShards.Shard4)]
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
[Category(MssqlCiShards.Shard4)]
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

file sealed class ChangeQueryAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ChangeQueryNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file sealed record TestTrackedChangeQueryRequest(
    ResourceInfo ResourceInfo,
    ChangeQueryEndpointOperation Operation,
    PaginationParameters PaginationParameters,
    ChangeVersionRange ChangeVersionRange,
    TraceId TraceId,
    RelationalAuthorizationContext AuthorizationContext,
    MappingSet MappingSet,
    ConcreteResourceModel ResourceModel,
    TrackedChangeTableInfo TrackedChangeTable
) : IRelationalTrackedChangeQueryRequest;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
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
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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

        if (_database is not null)
        {
            await _database.DisposeAsync();
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
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
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
                    DocumentUuid: SchoolDocumentUuid,
                    DocumentSecurityElements: new([], [], [], [], []),
                    UpdateCascadeHandler: new ChangeQueryNoOpUpdateCascadeHandler(),
                    ResourceAuthorizationHandler: new ChangeQueryAllowAllResourceAuthorizationHandler(),
                    ResourceAuthorizationPathways: []
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
                    DocumentUuid: AcademicWeekDocumentUuid,
                    DocumentSecurityElements: new([], [], [], [], []),
                    UpdateCascadeHandler: new ChangeQueryNoOpUpdateCascadeHandler(),
                    ResourceAuthorizationHandler: new ChangeQueryAllowAllResourceAuthorizationHandler(),
                    ResourceAuthorizationPathways: []
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
        var repository = new RelationalChangeQueryRepository(commandExecutor);
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
}
