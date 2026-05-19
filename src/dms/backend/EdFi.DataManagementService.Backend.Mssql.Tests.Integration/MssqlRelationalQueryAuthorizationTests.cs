// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlRelationalQueryAuthorizationAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlRelationalQueryAuthorizationNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed class MssqlRelationalQueryAuthorizationTestContext : IAsyncDisposable
{
    private const int MaximumPageSize = 500;
    private static readonly BaseResourceInfo SchoolResource = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("School"),
        false
    );
    private static readonly BaseResourceInfo ClassPeriodResource = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("ClassPeriod"),
        false
    );
    private readonly Dictionary<
        (string ProjectEndpointName, string ResourceName),
        ResourceHandle
    > _resourceCache = [];
    private readonly Dictionary<int, long> _schoolDocumentIdsBySchoolId = [];

    private MssqlGeneratedDdlFixture _fixture = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlRelationalQueryExecutionRecorder _recorder = null!;

    public MappingSet MappingSet => _fixture.MappingSet;

    public MssqlGeneratedDdlTestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync(
        string fixtureRelativePath,
        bool strict,
        bool replaceReadTargetLookup = true
    )
    {
        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(fixtureRelativePath, strict);
        Database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider(replaceReadTargetLookup);
        _recorder = _serviceProvider.GetRequiredService<MssqlRelationalQueryExecutionRecorder>();
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

    public PageKeysetSpec.Single AssertSingleDocumentHydration()
    {
        _recorder.HydrationKeysets.Should().ContainSingle();
        _recorder.HydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Single>();
        return (PageKeysetSpec.Single)_recorder.HydrationKeysets[0];
    }

    public void AssertSingleDocumentMaterialized()
    {
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(1);
        _recorder.PageMaterializationCallCount.Should().Be(0);
    }

    public void AssertNoHydration()
    {
        _recorder.HydrationKeysets.Should().BeEmpty();
        _recorder.PageMaterializationCallCount.Should().Be(0);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
    }

    public void AssertHydratedWithoutMaterialization(int expectedHydrationCount)
    {
        _recorder.HydrationKeysets.Should().HaveCount(expectedHydrationCount);
        _recorder.PageMaterializationCallCount.Should().Be(0);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
    }

    public void BeforeNextHydration(Func<CancellationToken, Task> beforeHydrationAsync)
    {
        _recorder.BeforeNextHydrationAsync = beforeHydrationAsync;
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
            RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                seed.SchoolId,
                seed.NameOfInstitution
            ),
            seed.DocumentUuid,
            $"seed-school-{seed.SchoolId}"
        );
    }

    public async Task<UpsertResult> CreateClassPeriodAsync(ClassPeriodSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "ClassPeriod",
            RelationalQueryAuthorizationRequestBodies.CreateClassPeriodRequestBody(seed),
            seed.DocumentUuid,
            $"seed-class-period-{seed.SchoolId}-{seed.ClassPeriodName}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationAndAsync(AuthorizationAndSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationAndResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationAndRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-and-{seed.AuthorizationAndId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationRootChildAsync(AuthorizationRootChildSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationRootChildResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-root-child-{seed.AuthorizationRootChildId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationChildOnlyAsync(AuthorizationChildOnlySeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationChildOnlyResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationChildOnlyRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-child-only-{seed.AuthorizationChildOnlyId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationNullableAsync(AuthorizationNullableSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationNullableResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationNullableRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-nullable-{seed.AuthorizationNullableId}"
        );
    }

    public async Task SeedSchoolReferenceResourceAsync(QuerySchoolSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
                    VALUES (@documentId, @nameOfInstitution, @schoolId);
                    """,
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@nameOfInstitution", seed.NameOfInstitution),
                    new SqlParameter("@schoolId", seed.SchoolId)
                )
        );

        await UpsertReferentialIdentityAsync(
            CreateSchoolReferentialId(seed.SchoolId),
            documentId,
            resourceKeyId
        );

        _schoolDocumentIdsBySchoolId[seed.SchoolId] = documentId;
    }

    public async Task SeedClassPeriodReferenceResourceAsync(ClassPeriodSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "ClassPeriod");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);

        if (!_schoolDocumentIdsBySchoolId.TryGetValue(seed.SchoolId, out var schoolDocumentId))
        {
            throw new InvalidOperationException(
                $"School '{seed.SchoolId}' must be seeded before ClassPeriod '{seed.ClassPeriodName}'."
            );
        }

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "ClassPeriod",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[ClassPeriod] (
                        [DocumentId],
                        [ClassPeriodName],
                        [School_DocumentId],
                        [School_SchoolId]
                    )
                    VALUES (
                        @documentId,
                        @classPeriodName,
                        @schoolDocumentId,
                        @schoolId
                    );
                    """,
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@classPeriodName", seed.ClassPeriodName),
                    new SqlParameter("@schoolDocumentId", schoolDocumentId),
                    new SqlParameter("@schoolId", seed.SchoolId)
                )
        );

        await UpsertReferentialIdentityAsync(CreateClassPeriodReferentialId(seed), documentId, resourceKeyId);
    }

    public async Task InsertAuthEdgeAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] (
                [SourceEducationOrganizationId],
                [TargetEducationOrganizationId]
            )
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
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

    public async Task<GetResult> GetByIdAsync(
        string projectEndpointName,
        string resourceName,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? traceId = null
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new IntegrationRelationalGetRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: resourceHandle.ResourceInfo,
            MappingSet: MappingSet,
            ResourceAuthorizationHandler: new RelationalQueryAuthorizationAllowAllResourceAuthorizationHandler(),
            AuthorizationStrategyEvaluators:
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
            TraceId: new TraceId(traceId ?? $"{resourceName}-authorization-get-by-id")
        )
        {
            AuthorizationContext = new RelationalAuthorizationContext(claimEducationOrganizationIds),
        };

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    public async Task<IReadOnlyList<PersistedQuerySchool>> ReadPersistedSchoolsInDocumentOrderAsync()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKeyId = MappingSet.ResourceKeyIdByResource[schoolResource];
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var rows = await Database.QueryRowsAsync(
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

    public async Task MutateAuthorizationRootChildSchoolAsync(
        DocumentUuid documentUuid,
        int newSchoolId,
        CancellationToken cancellationToken = default
    )
    {
        _ = cancellationToken;
        var documentId = await GetDocumentIdByUuidAsync(documentUuid);
        var schoolDocumentId = await GetSchoolDocumentIdAsync(newSchoolId);

        await Database.ExecuteNonQueryAsync(
            """
            UPDATE [authz].[AuthorizationRootChildResource]
            SET
                [School_DocumentId] = @schoolDocumentId,
                [School_SchoolId] = @newSchoolId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@newSchoolId", newSchoolId),
            new SqlParameter("@documentId", documentId)
        );
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
            UpdateCascadeHandler: new MssqlRelationalQueryAuthorizationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlRelationalQueryAuthorizationAllowAllResourceAuthorizationHandler(),
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

    private static ServiceProvider CreateServiceProvider(bool replaceReadTargetLookup)
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

        if (replaceReadTargetLookup)
        {
            services.Replace(
                ServiceDescriptor.Scoped<
                    IRelationalReadTargetLookupService,
                    ThrowingRelationalReadTargetLookupService
                >()
            );
        }

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
                    InstanceName: "MssqlRelationalQueryAuthorization",
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

        await UpsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", resourceName, uri),
            documentId,
            resourceKeyId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await Database.ExecuteScalarAsync<short>(
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

    private async Task<long> GetDocumentIdByUuidAsync(DocumentUuid documentUuid)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
    }

    private async Task<long> GetSchoolDocumentIdAsync(int schoolId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [edfi].[School]
            WHERE [SchoolId] = @schoolId;
            """,
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await Database.ExecuteScalarAsync<long>(
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

    private async Task UpsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            MERGE [dms].[ReferentialIdentity] AS target
            USING (
                SELECT
                    @referentialId AS [ReferentialId],
                    @documentId AS [DocumentId],
                    @resourceKeyId AS [ResourceKeyId]
            ) AS source
                ON target.[ReferentialId] = source.[ReferentialId]
            WHEN NOT MATCHED THEN
                INSERT ([ReferentialId], [DocumentId], [ResourceKeyId])
                VALUES (source.[ReferentialId], source.[DocumentId], source.[ResourceKeyId]);
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

    private static ReferentialId CreateSchoolReferentialId(int schoolId)
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                schoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return ReferentialIdCalculator.ReferentialIdFrom(SchoolResource, schoolIdentity);
    }

    private static ReferentialId CreateClassPeriodReferentialId(ClassPeriodSeed seed)
    {
        var classPeriodIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.classPeriodName"), seed.ClassPeriodName),
            new DocumentIdentityElement(
                new JsonPath("$.schoolReference.schoolId"),
                seed.SchoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return ReferentialIdCalculator.ReferentialIdFrom(ClassPeriodResource, classPeriodIdentity);
    }

    private async Task ExecuteWithTriggersTemporarilyDisabledAsync(
        string schema,
        string table,
        Func<Task> action
    )
    {
        await Database.ExecuteNonQueryAsync($"""DISABLE TRIGGER ALL ON [{schema}].[{table}];""");

        try
        {
            await action();
        }
        finally
        {
            await Database.ExecuteNonQueryAsync($"""ENABLE TRIGGER ALL ON [{schema}].[{table}];""");
        }
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
[Category("MssqlIntegration")]
public class Given_A_Mssql_Relational_Query_Authorization_With_The_Authoritative_Ds52_School_Fixture
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

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;
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

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            await _context.SeedSchoolReferenceResourceAsync(schoolSeed);
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
            .Contain("@ClaimEducationOrganizationIds_0")
            .And.Contain("[TargetEducationOrganizationId]")
            .And.Contain("[SchoolId]");
        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(ClaimEducationOrganizationId);
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
            .Contain("[SourceEducationOrganizationId]")
            .And.Contain("[TargetEducationOrganizationId]");
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

    [Test]
    public async Task It_uses_expanded_scalar_parameters_for_1999_unique_claim_edorg_ids()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            CreateUniqueClaimEducationOrganizationIds(1999),
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);

        var keyset = _context.AssertSingleQueryHydration();
        AssertScalarFilterParameters(keyset, 1999, 900L, 2997L);
    }

    [Test]
    public async Task It_uses_a_structured_tvp_for_2000_unique_claim_edorg_ids()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            CreateUniqueClaimEducationOrganizationIds(2000),
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);

        var keyset = _context.AssertSingleQueryHydration();
        AssertStructuredFilterParameter(keyset, 2000, 900L, 2998L);
    }

    [Test]
    public async Task It_deduplicates_duplicate_heavy_claim_edorg_ids_before_using_the_scalar_threshold()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            CreateDuplicateHeavyClaimEducationOrganizationIds(),
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);

        var keyset = _context.AssertSingleQueryHydration();
        AssertScalarFilterParameters(keyset, 1999, 900L, 2997L);
        keyset
            .ParameterValues.ContainsKey(
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
            .Should()
            .BeFalse();
    }

    private static IReadOnlyList<long> CreateUniqueClaimEducationOrganizationIds(int uniqueCount)
    {
        if (uniqueCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(uniqueCount));
        }

        return
        [
            ClaimEducationOrganizationId,
            .. Enumerable.Range(0, uniqueCount - 1).Select(static index => 1000L + index),
        ];
    }

    private static IReadOnlyList<long> CreateDuplicateHeavyClaimEducationOrganizationIds()
    {
        var uniqueClaimIds = CreateUniqueClaimEducationOrganizationIds(1999);

        return [.. uniqueClaimIds, .. uniqueClaimIds.Take(50), .. uniqueClaimIds.Take(50)];
    }

    private static void AssertScalarFilterParameters(
        PageKeysetSpec.Query keyset,
        int expectedCount,
        long expectedFirstValue,
        long expectedLastValue
    )
    {
        var pageFilterParameters = GetPageFilterParameters(keyset);
        pageFilterParameters.Should().HaveCount(expectedCount);
        pageFilterParameters[0].ParameterName.Should().Be("ClaimEducationOrganizationIds_0");
        pageFilterParameters[^1]
            .ParameterName.Should()
            .Be($"ClaimEducationOrganizationIds_{expectedCount - 1}");
        pageFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);

        var totalCountFilterParameters = GetTotalCountFilterParameters(keyset);
        totalCountFilterParameters.Should().HaveCount(expectedCount);
        totalCountFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);

        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(expectedFirstValue);
        keyset
            .ParameterValues[$"ClaimEducationOrganizationIds_{expectedCount - 1}"]
            .Should()
            .Be(expectedLastValue);
    }

    private static void AssertStructuredFilterParameter(
        PageKeysetSpec.Query keyset,
        int expectedCount,
        long expectedFirstValue,
        long expectedLastValue
    )
    {
        var pageFilterParameters = GetPageFilterParameters(keyset);
        pageFilterParameters.Should().ContainSingle();
        pageFilterParameters[0]
            .ParameterName.Should()
            .Be(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds);
        pageFilterParameters[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.MssqlStructured);
        pageFilterParameters[0].Binding.StructuredTypeName.Should().Be("dms.BigIntTable");
        pageFilterParameters[0].Binding.StructuredColumnName.Should().Be("Id");

        var totalCountFilterParameters = GetTotalCountFilterParameters(keyset);
        totalCountFilterParameters.Should().ContainSingle();
        totalCountFilterParameters[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.MssqlStructured);

        keyset.Plan.PageDocumentIdSql.Should().Contain("SELECT [Id] FROM @ClaimEducationOrganizationIds");

        keyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .HaveCount(expectedCount)
            .And.StartWith(expectedFirstValue)
            .And.EndWith(expectedLastValue);
    }

    private static IReadOnlyList<QuerySqlParameter> GetPageFilterParameters(PageKeysetSpec.Query keyset)
    {
        return
        [
            .. keyset.Plan.PageParametersInOrder.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];
    }

    private static IReadOnlyList<QuerySqlParameter> GetTotalCountFilterParameters(PageKeysetSpec.Query keyset)
    {
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();

        return
        [
            .. keyset.Plan.TotalCountParametersInOrder!.Value.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Relational_Query_Authorization_With_A_Synthetic_EdOrg_Fixture
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

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: false);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            await _context.SeedSchoolReferenceResourceAsync(schoolSeed);
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            await _context.SeedClassPeriodReferenceResourceAsync(classPeriodSeed);
        }

        foreach (var authorizationAndSeed in _authorizationAndSeeds)
        {
            var createResult = await _context.CreateAuthorizationAndAsync(authorizationAndSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var authorizationRootChildSeed in _authorizationRootChildSeeds)
        {
            var createResult = await _context.CreateAuthorizationRootChildAsync(authorizationRootChildSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
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
