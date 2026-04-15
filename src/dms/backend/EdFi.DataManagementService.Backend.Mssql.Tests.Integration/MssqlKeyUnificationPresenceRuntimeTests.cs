// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
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

file sealed class MssqlKeyUnifAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlKeyUnifNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed record MssqlKeyUnifDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MssqlKeyUnifWidgetRow(
    long DocumentId,
    int WidgetId,
    string? Name,
    string? PrimaryTypeUnified,
    bool? PrimaryTypePresent,
    bool? SecondaryTypePresent
);

internal sealed record MssqlKeyUnifPersistedState(
    MssqlKeyUnifDocumentRow Document,
    MssqlKeyUnifWidgetRow Widget,
    long DocumentCount
);

file static class MssqlKeyUnifTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/key-unification-presence";

    public static readonly QualifiedResourceName WidgetResource = new("Ed-Fi", "Widget");

    public static readonly ResourceInfo WidgetResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Widget"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public const string InitialCreateBodyJson =
        """{"widgetId": 42, "primaryType": "Alpha", "secondaryType": "Alpha", "name": "TestWidget"}""";

    public static ServiceProvider CreateServiceProvider()
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

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string? requestBodyJsonOverride = null,
        BackendProfileWriteContext? profileContext = null
    ) =>
        new(
            ResourceInfo: WidgetResourceInfo,
            DocumentInfo: CreateWidgetDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJsonOverride ?? InitialCreateBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlKeyUnifNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlKeyUnifAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string requestBodyJson,
        BackendProfileWriteContext? profileContext = null
    ) =>
        new(
            ResourceInfo: WidgetResourceInfo,
            DocumentInfo: CreateWidgetDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlKeyUnifNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlKeyUnifAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

    public static DocumentInfo CreateWidgetDocumentInfo()
    {
        var widgetIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.widgetId"), "42"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: widgetIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(WidgetResourceInfo, widgetIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public static async Task<MssqlKeyUnifPersistedState> ReadFullPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var widget = await ReadWidgetAsync(database, document.DocumentId);
        var documentCount = await ReadDocumentCountAsync(database);

        return new MssqlKeyUnifPersistedState(document, widget, documentCount);
    }

    public static async Task<long> ReadDocumentCountAsync(MssqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS [Count]
            FROM [dms].[Document];
            """
        );

        return rows.Count == 1
            ? GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    public static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    public static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    public static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value as string
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    public static bool? GetNullableBoolean(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value switch
            {
                null => null,
                bool boolValue => boolValue,
                _ => throw new InvalidOperationException(
                    $"Expected column '{columnName}' to contain a boolean value."
                ),
            }
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    private static async Task<MssqlKeyUnifDocumentRow> ReadDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new MssqlKeyUnifDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<MssqlKeyUnifWidgetRow> ReadWidgetAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [WidgetId], [Name], [PrimaryType_Unified], [PrimaryType_Present], [SecondaryType_Present]
            FROM [edfi].[Widget]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new MssqlKeyUnifWidgetRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt32(rows[0], "WidgetId"),
                GetNullableString(rows[0], "Name"),
                GetNullableString(rows[0], "PrimaryType_Unified"),
                GetNullableBoolean(rows[0], "PrimaryType_Present"),
                GetNullableBoolean(rows[0], "SecondaryType_Present")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one widget row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException(
                $"Expected persisted row to contain non-null column '{columnName}'."
            );
        }

        return value;
    }
}

file sealed class MssqlKeyUnifFixedStoredStateProjectionInvoker(
    ImmutableArray<StoredScopeState> storedScopeStates,
    ImmutableArray<VisibleStoredCollectionRow> visibleStoredCollectionRows
) : IStoredStateProjectionInvoker
{
    private readonly ImmutableArray<StoredScopeState> _storedScopeStates = storedScopeStates;
    private readonly ImmutableArray<VisibleStoredCollectionRow> _visibleStoredCollectionRows =
        visibleStoredCollectionRows;

    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) =>
        new(
            Request: request,
            VisibleStoredBody: storedDocument.DeepClone(),
            StoredScopeStates: _storedScopeStates,
            VisibleStoredCollectionRows: _visibleStoredCollectionRows
        );
}

file sealed class MssqlKeyUnifUnexpectedStoredStateProjectionInvoker : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) =>
        throw new InvalidOperationException(
            "Stored-state projection should not run for create-new profiled requests."
        );
}

file static class MssqlKeyUnifContextFactory
{
    private static readonly QualifiedResourceName WidgetResource = new("Ed-Fi", "Widget");

    /// <summary>
    /// Creates a profile context for hidden key-unified canonical preservation:
    /// secondaryType is hidden, primaryType is visible. The stored state reflects
    /// both members were present during the initial (non-profiled) create.
    /// </summary>
    public static BackendProfileWriteContext CreateHiddenCanonicalPreservationContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[WidgetResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: requestBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "key-unif-canonical-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlKeyUnifFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: ["secondaryType"]
                    ),
                ],
                visibleStoredCollectionRows: []
            )
        );
    }

    /// <summary>
    /// Creates a profile context for hidden synthetic presence preservation:
    /// primaryType is hidden, secondaryType is visible but absent from the update body.
    /// The stored state reflects both members were present during the initial (non-profiled) create.
    /// </summary>
    public static BackendProfileWriteContext CreateHiddenPresencePreservationContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[WidgetResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: requestBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "key-unif-presence-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlKeyUnifFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: ["primaryType"]
                    ),
                ],
                visibleStoredCollectionRows: []
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Key_Unified_Canonical_Preservation
{
    private static readonly DocumentUuid WidgetDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000401")
    );

    private const string UpdateBodyJson =
        """{"widgetId": 42, "primaryType": "Beta", "name": "UpdatedWidget"}""";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlKeyUnifPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlKeyUnifTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlKeyUnifTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await MssqlKeyUnifTestSupport.ReadFullPersistedStateAsync(
            _database,
            WidgetDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
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

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlKeyUnifCanonicalPreservation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlKeyUnifTestSupport.CreateCreateRequest(
                _mappingSet,
                WidgetDocumentUuid,
                "mssql-key-unif-canonical-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteProfiledUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlKeyUnifCanonicalPreservation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlKeyUnifTestSupport.CreateUpdateRequest(
                _mappingSet,
                WidgetDocumentUuid,
                "mssql-key-unif-canonical-update",
                UpdateBodyJson,
                MssqlKeyUnifContextFactory.CreateHiddenCanonicalPreservationContext(
                    _mappingSet,
                    UpdateBodyJson
                )
            )
        );
    }

    [Test]
    public void It_returns_update_success()
    {
        var failureMessage = _updateResult is UpdateResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "key-unified canonical preservation update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(WidgetDocumentUuid);
    }

    [Test]
    public void It_updates_the_canonical_to_visible_value()
    {
        _stateAfterUpdate.Widget.PrimaryTypeUnified.Should().Be("Beta");
    }

    [Test]
    public void It_preserves_the_hidden_presence_flag()
    {
        _stateAfterUpdate.Widget.SecondaryTypePresent.Should().BeTrue();
    }

    [Test]
    public void It_updates_the_visible_presence_flag()
    {
        _stateAfterUpdate.Widget.PrimaryTypePresent.Should().BeTrue();
    }

    [Test]
    public void It_updates_the_name()
    {
        _stateAfterUpdate.Widget.Name.Should().Be("UpdatedWidget");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Synthetic_Presence_Preservation
{
    private static readonly DocumentUuid WidgetDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000402")
    );

    private const string UpdateBodyJson = """{"widgetId": 42, "name": "StillHere"}""";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlKeyUnifPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlKeyUnifTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlKeyUnifTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await MssqlKeyUnifTestSupport.ReadFullPersistedStateAsync(
            _database,
            WidgetDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
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

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlKeyUnifPresencePreservation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlKeyUnifTestSupport.CreateCreateRequest(
                _mappingSet,
                WidgetDocumentUuid,
                "mssql-key-unif-presence-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteProfiledUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlKeyUnifPresencePreservation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlKeyUnifTestSupport.CreateUpdateRequest(
                _mappingSet,
                WidgetDocumentUuid,
                "mssql-key-unif-presence-update",
                UpdateBodyJson,
                MssqlKeyUnifContextFactory.CreateHiddenPresencePreservationContext(
                    _mappingSet,
                    UpdateBodyJson
                )
            )
        );
    }

    [Test]
    public void It_returns_update_success()
    {
        var failureMessage = _updateResult is UpdateResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "synthetic presence preservation update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(WidgetDocumentUuid);
    }

    [Test]
    public void It_preserves_the_hidden_presence_flag()
    {
        _stateAfterUpdate.Widget.PrimaryTypePresent.Should().BeTrue();
    }

    [Test]
    public void It_preserves_the_canonical_from_hidden_contributor()
    {
        _stateAfterUpdate.Widget.PrimaryTypeUnified.Should().Be("Alpha");
    }

    [Test]
    public void It_updates_the_name()
    {
        _stateAfterUpdate.Widget.Name.Should().Be("StillHere");
    }
}
