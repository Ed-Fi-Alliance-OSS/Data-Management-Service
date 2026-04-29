// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Mssql;
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

file sealed class MssqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        System.Text.Json.JsonElement originalEdFiDoc,
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

internal sealed class MssqlProfileCollectionAlignedExtensionProjectionInvoker(
    ImmutableArray<MssqlProfileCollectionAlignedExtensionStoredParentRow> storedParentRows,
    ImmutableArray<MssqlProfileCollectionAlignedExtensionStoredAlignedScope> storedAlignedScopes
) : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var storedScopeStates = ImmutableArray.CreateBuilder<StoredScopeState>();
        storedScopeStates.Add(
            new StoredScopeState(new ScopeInstanceAddress("$", []), ProfileVisibilityKind.VisiblePresent, [])
        );
        storedScopeStates.AddRange(
            storedAlignedScopes.Select(scope => new StoredScopeState(
                MssqlProfileCollectionAlignedExtensionSupport.AlignedScopeAddress(scope.ParentCode),
                scope.Visibility,
                scope.HiddenMemberPaths
            ))
        );

        var visibleStoredRows = storedParentRows
            .Select(row => new VisibleStoredCollectionRow(
                MssqlProfileCollectionAlignedExtensionSupport.ParentCollectionRowAddress(row.ParentCode),
                row.HiddenMemberPaths
            ))
            .ToImmutableArray();

        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates: storedScopeStates.ToImmutable(),
            VisibleStoredCollectionRows: visibleStoredRows
        );
    }
}

internal sealed record MssqlProfileCollectionAlignedExtensionParentInput(
    string ParentCode,
    string ParentName,
    MssqlProfileCollectionAlignedExtensionAlignedInput? Aligned = null
);

internal sealed record MssqlProfileCollectionAlignedExtensionAlignedInput(
    string AlignedVisibleScalar,
    string AlignedHiddenScalar
);

internal sealed record MssqlProfileCollectionAlignedExtensionRequestParentItem(
    string ParentCode,
    int ArrayIndex,
    bool Creatable = true
);

internal sealed record MssqlProfileCollectionAlignedExtensionRequestAlignedScope(
    string ParentCode,
    ProfileVisibilityKind Visibility,
    bool Creatable
);

internal sealed record MssqlProfileCollectionAlignedExtensionStoredParentRow(
    string ParentCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileCollectionAlignedExtensionStoredAlignedScope(
    string ParentCode,
    ProfileVisibilityKind Visibility,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileCollectionAlignedExtensionParentRow(
    long CollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string ParentCode,
    string ParentName
);

internal sealed record MssqlProfileCollectionAlignedExtensionAlignedRow(
    long BaseCollectionItemId,
    long ParentResourceDocumentId,
    string? AlignedVisibleScalar,
    string? AlignedHiddenScalar
);

internal static class MssqlProfileCollectionAlignedExtensionSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension";

    public const string ParentScope = "$.parents[*]";
    public const string AlignedScope = "$.parents[*]._ext.aligned";

    public static readonly QualifiedResourceName ParentResource = new("Ed-Fi", "ParentResource");

    public static readonly ResourceInfo ParentResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ParentResource"),
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
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static JsonNode CreateParentResourceBody(
        int parentResourceId,
        params MssqlProfileCollectionAlignedExtensionParentInput[] parents
    )
    {
        JsonArray parentNodes = [];
        foreach (var parent in parents)
        {
            JsonObject parentNode = new()
            {
                ["parentCode"] = parent.ParentCode,
                ["parentName"] = parent.ParentName,
            };

            if (parent.Aligned is not null)
            {
                parentNode["_ext"] = new JsonObject
                {
                    ["aligned"] = new JsonObject
                    {
                        ["alignedVisibleScalar"] = parent.Aligned.AlignedVisibleScalar,
                        ["alignedHiddenScalar"] = parent.Aligned.AlignedHiddenScalar,
                    },
                };
            }

            parentNodes.Add(parentNode);
        }

        return new JsonObject { ["parentResourceId"] = parentResourceId, ["parents"] = parentNodes };
    }

    public static DocumentInfo CreateDocumentInfo(int parentResourceId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.parentResourceId"),
                parentResourceId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(ParentResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public static ImmutableArray<SemanticIdentityPart> ParentIdentity(string parentCode) =>
        [new SemanticIdentityPart("parentCode", JsonValue.Create(parentCode), IsPresent: true)];

    public static CollectionRowAddress ParentCollectionRowAddress(string parentCode) =>
        new(ParentScope, new ScopeInstanceAddress("$", []), ParentIdentity(parentCode));

    public static ScopeInstanceAddress ParentContainingScopeAddress(string parentCode) =>
        new(ParentScope, [new AncestorCollectionInstance(ParentScope, ParentIdentity(parentCode))]);

    public static ScopeInstanceAddress AlignedScopeAddress(string parentCode) =>
        new(AlignedScope, ParentContainingScopeAddress(parentCode).AncestorCollectionInstances);

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionRequestParentItem> requestParentItems,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionRequestAlignedScope> requestAlignedScopes,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionStoredParentRow> storedParentRows,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionStoredAlignedScope> storedAlignedScopes,
        bool rootCreatable = true,
        string profileName = "collection-aligned-extension-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var visibleRequestItems = requestParentItems
            .Select(item => new VisibleRequestCollectionItem(
                ParentCollectionRowAddress(item.ParentCode),
                item.Creatable,
                $"$.parents[{item.ArrayIndex}]"
            ))
            .ToImmutableArray();

        var requestScopeStates = ImmutableArray.CreateBuilder<RequestScopeState>();
        requestScopeStates.Add(
            new RequestScopeState(
                new ScopeInstanceAddress("$", []),
                ProfileVisibilityKind.VisiblePresent,
                rootCreatable
            )
        );
        requestScopeStates.AddRange(
            requestAlignedScopes.Select(scope => new RequestScopeState(
                AlignedScopeAddress(scope.ParentCode),
                scope.Visibility,
                scope.Creatable
            ))
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates: requestScopeStates.ToImmutable(),
                VisibleRequestCollectionItems: visibleRequestItems
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlProfileCollectionAlignedExtensionProjectionInvoker(
                [.. storedParentRows],
                [.. storedAlignedScopes]
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int parentResourceId,
        JsonNode body,
        DocumentUuid documentUuid,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileCollectionAlignedExtension",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: ParentResourceInfo,
            DocumentInfo: CreateDocumentInfo(parentResourceId),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpsertResult> ExecuteProfiledPostAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int parentResourceId,
        JsonNode writeBody,
        DocumentUuid documentUuid,
        BackendProfileWriteContext profileContext,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileCollectionAlignedExtension",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: ParentResourceInfo,
            DocumentInfo: CreateDocumentInfo(parentResourceId),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int parentResourceId,
        JsonNode writeBody,
        DocumentUuid documentUuid,
        BackendProfileWriteContext profileContext,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileCollectionAlignedExtension",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );

        var updateRequest = new UpdateRequest(
            ResourceInfo: ParentResourceInfo,
            DocumentInfo: CreateDocumentInfo(parentResourceId),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<long> ReadDocumentIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

        rows.Should().HaveCount(1);
        return GetInt64(rows[0], "DocumentId");
    }

    public static Task<long> ReadDocumentCountAsync(MssqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM [dms].[Document];
            """
        );

    public static async Task<
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionParentRow>
    > ReadParentRowsAsync(MssqlGeneratedDdlTestDatabase database, DocumentUuid documentUuid)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                p.[CollectionItemId],
                p.[ParentResource_DocumentId],
                p.[Ordinal],
                p.[ParentCode],
                p.[ParentName]
            FROM [edfi].[ParentResourceParent] p
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = p.[ParentResource_DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid
            ORDER BY p.[Ordinal], p.[CollectionItemId];
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new MssqlProfileCollectionAlignedExtensionParentRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "ParentCode"),
                GetString(row, "ParentName")
            ))
            .ToArray();
    }

    public static async Task<MssqlProfileCollectionAlignedExtensionAlignedRow?> TryReadAlignedRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ext.[BaseCollectionItemId],
                ext.[ParentResource_DocumentId],
                ext.[AlignedVisibleScalar],
                ext.[AlignedHiddenScalar]
            FROM [aligned].[ParentResourceExtensionParent] ext
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ext.[ParentResource_DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

        return rows.Count == 0
            ? null
            : new MssqlProfileCollectionAlignedExtensionAlignedRow(
                GetInt64(rows[0], "BaseCollectionItemId"),
                GetInt64(rows[0], "ParentResource_DocumentId"),
                GetNullableString(rows[0], "AlignedVisibleScalar"),
                GetNullableString(rows[0], "AlignedHiddenScalar")
            );
    }

    public static async Task<int> CountAlignedRowsAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var scalar = await database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM [aligned].[ParentResourceExtensionParent] ext
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ext.[ParentResource_DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
        return checked((int)scalar);
    }

    public static Task<long> ReadAlignedChildRowCountAsync(MssqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM [aligned].[ParentResourceExtensionParentChildren];
            """
        );

    private static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(row[columnName], CultureInfo.InvariantCulture);

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(row[columnName], CultureInfo.InvariantCulture);

    private static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row[columnName]?.ToString()
        ?? throw new InvalidOperationException($"Column '{columnName}' was null.");

    private static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row[columnName]?.ToString();
}

internal abstract class MssqlProfileCollectionAlignedExtensionFixtureBase
{
    protected const int ParentResourceId = 9301;
    protected const string ParentCode = "PARENT-A";
    protected static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("ff000001-0000-0000-0000-000000000001")
    );

    protected MssqlGeneratedDdlFixture Fixture = null!;
    protected MappingSet MappingSet = null!;
    protected MssqlGeneratedDdlTestDatabase Database = null!;
    protected ServiceProvider ServiceProvider = null!;

    [OneTimeSetUp]
    public async Task BaseOneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        Fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileCollectionAlignedExtensionSupport.FixtureRelativePath
        );
        MappingSet = Fixture.MappingSet;
        Database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(Fixture.GeneratedDdl);
        ServiceProvider = MssqlProfileCollectionAlignedExtensionSupport.CreateServiceProvider();
    }

    [OneTimeTearDown]
    public async Task BaseOneTimeTearDown()
    {
        if (ServiceProvider is not null)
        {
            await ServiceProvider.DisposeAsync();
        }
        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }

    protected ResourceWritePlan WritePlan =>
        MappingSet.WritePlansByResource[MssqlProfileCollectionAlignedExtensionSupport.ParentResource];

    protected BackendProfileWriteContext CreateProfileContext(
        JsonNode requestBody,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionRequestParentItem> requestParentItems,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionRequestAlignedScope> requestAlignedScopes,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionStoredParentRow>? storedParentRows = null,
        IReadOnlyList<MssqlProfileCollectionAlignedExtensionStoredAlignedScope>? storedAlignedScopes = null
    ) =>
        MssqlProfileCollectionAlignedExtensionSupport.CreateProfileContext(
            WritePlan,
            requestBody.DeepClone(),
            requestParentItems,
            requestAlignedScopes,
            storedParentRows ?? [],
            storedAlignedScopes ?? []
        );

    protected Task<UpsertResult> ExecuteProfiledPostAsync(
        JsonNode writeBody,
        BackendProfileWriteContext profileContext,
        string traceLabel
    ) =>
        MssqlProfileCollectionAlignedExtensionSupport.ExecuteProfiledPostAsync(
            ServiceProvider,
            Database,
            MappingSet,
            ParentResourceId,
            writeBody,
            DocumentUuid,
            profileContext,
            traceLabel
        );

    protected Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        BackendProfileWriteContext profileContext,
        string traceLabel
    ) =>
        MssqlProfileCollectionAlignedExtensionSupport.ExecuteProfiledPutAsync(
            ServiceProvider,
            Database,
            MappingSet,
            ParentResourceId,
            writeBody,
            DocumentUuid,
            profileContext,
            traceLabel
        );

    protected async Task SeedAsync(JsonNode seedBody, string traceLabel)
    {
        var seedResult = await MssqlProfileCollectionAlignedExtensionSupport.SeedAsync(
            ServiceProvider,
            Database,
            MappingSet,
            ParentResourceId,
            seedBody,
            DocumentUuid,
            traceLabel
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    protected static MssqlProfileCollectionAlignedExtensionRequestParentItem RequestParent() =>
        new(ParentCode, ArrayIndex: 0);

    protected static MssqlProfileCollectionAlignedExtensionStoredParentRow StoredParent() =>
        new(ParentCode, []);

    protected static MssqlProfileCollectionAlignedExtensionRequestAlignedScope RequestAligned(
        ProfileVisibilityKind visibility,
        bool creatable
    ) => new(ParentCode, visibility, creatable);

    protected static MssqlProfileCollectionAlignedExtensionStoredAlignedScope StoredAligned(
        ProfileVisibilityKind visibility
    ) => new(ParentCode, visibility, []);

    protected static string FormatResult(UpsertResult result) =>
        result switch
        {
            UpsertResult.UnknownFailure failure => failure.FailureMessage,
            _ => result.ToString() ?? result.GetType().Name,
        };

    protected static string FormatResult(UpdateResult result) =>
        result switch
        {
            UpdateResult.UnknownFailure failure => failure.FailureMessage,
            _ => result.ToString() ?? result.GetType().Name,
        };
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_create_request_for_a_resource_with_a_visible_present_aligned_extension_scope
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private IReadOnlyList<MssqlProfileCollectionAlignedExtensionParentRow> _parentRows = null!;
    private MssqlProfileCollectionAlignedExtensionAlignedRow? _alignedRow;
    private long _alignedChildRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Created Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("CreatedVisible", "CreatedHidden")
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)]
        );

        _postResult = await ExecuteProfiledPostAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-visible-present-post"
        );
        _parentRows = await MssqlProfileCollectionAlignedExtensionSupport.ReadParentRowsAsync(
            Database,
            DocumentUuid
        );
        _alignedRow = await MssqlProfileCollectionAlignedExtensionSupport.TryReadAlignedRowAsync(
            Database,
            DocumentUuid
        );
        _alignedChildRowCount =
            await MssqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowCountAsync(Database);
    }

    [Test]
    public void It_returns_insert_success() =>
        _postResult.Should().BeOfType<UpsertResult.InsertSuccess>(FormatResult(_postResult));

    [Test]
    public void It_inserts_the_parent_row() =>
        _parentRows
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<MssqlProfileCollectionAlignedExtensionParentRow>(row =>
                row.Ordinal == 1 && row.ParentCode == ParentCode && row.ParentName == "Created Parent"
            );

    [Test]
    public void It_inserts_the_aligned_extension_row()
    {
        var parentRow = _parentRows.Should().ContainSingle().Subject;
        _alignedRow.Should().NotBeNull();
        _alignedRow!.BaseCollectionItemId.Should().Be(parentRow.CollectionItemId);
        _alignedRow.ParentResourceDocumentId.Should().Be(parentRow.ParentResourceDocumentId);
        _alignedRow.AlignedVisibleScalar.Should().Be("CreatedVisible");
        _alignedRow.AlignedHiddenScalar.Should().Be("CreatedHidden");
    }

    [Test]
    public void It_does_not_touch_aligned_child_collections() => _alignedChildRowCount.Should().Be(0);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_create_request_for_a_resource_with_a_visible_absent_aligned_extension_scope
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Created Parent")
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisibleAbsent, creatable: true)]
        );

        _postResult = await ExecuteProfiledPostAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-visible-absent-post"
        );
        _alignedRowCount = await MssqlProfileCollectionAlignedExtensionSupport.CountAlignedRowsAsync(
            Database,
            DocumentUuid
        );
    }

    [Test]
    public void It_returns_insert_success() =>
        _postResult.Should().BeOfType<UpsertResult.InsertSuccess>(FormatResult(_postResult));

    [Test]
    public void It_does_not_insert_the_aligned_extension_row() => _alignedRowCount.Should().Be(0);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_create_request_for_a_resource_with_a_hidden_aligned_extension_scope_and_request_data
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private long _documentCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Hidden Request Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("HiddenVisible", "HiddenHidden")
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.Hidden, creatable: false)]
        );

        _postResult = await ExecuteProfiledPostAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-hidden-request-data-post"
        );
        _documentCount = await MssqlProfileCollectionAlignedExtensionSupport.ReadDocumentCountAsync(Database);
    }

    [Test]
    public void It_returns_profile_data_policy_failure() =>
        _postResult.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>(FormatResult(_postResult));

    [Test]
    public void It_rolls_back_the_write() => _documentCount.Should().Be(0);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_visible_present_to_visible_present
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private MssqlProfileCollectionAlignedExtensionAlignedRow? _alignedRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("SeedVisible", "SeedHidden")
            )
        );
        await SeedAsync(seedBody, "mssql-profile-collection-aligned-visible-present-seed");

        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("UpdatedVisible", "UpdatedHidden")
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-visible-present-put"
        );
        _alignedRow = await MssqlProfileCollectionAlignedExtensionSupport.TryReadAlignedRowAsync(
            Database,
            DocumentUuid
        );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_updates_the_aligned_extension_row()
    {
        _alignedRow.Should().NotBeNull();
        _alignedRow!.AlignedVisibleScalar.Should().Be("UpdatedVisible");
        _alignedRow.AlignedHiddenScalar.Should().Be("UpdatedHidden");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_visible_present_to_visible_absent
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("SeedVisible", "SeedHidden")
            )
        );
        await SeedAsync(seedBody, "mssql-profile-collection-aligned-visible-absent-seed");

        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Updated Parent")
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisibleAbsent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-visible-absent-put"
        );
        _alignedRowCount = await MssqlProfileCollectionAlignedExtensionSupport.CountAlignedRowsAsync(
            Database,
            DocumentUuid
        );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_deletes_the_aligned_extension_row() => _alignedRowCount.Should().Be(0);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_hidden_in_storage
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private MssqlProfileCollectionAlignedExtensionAlignedRow? _alignedRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("StoredVisible", "StoredHidden")
            )
        );
        await SeedAsync(seedBody, "mssql-profile-collection-aligned-hidden-storage-seed");

        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Updated Parent")
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.Hidden, creatable: false)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.Hidden)]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-hidden-storage-put"
        );
        _alignedRow = await MssqlProfileCollectionAlignedExtensionSupport.TryReadAlignedRowAsync(
            Database,
            DocumentUuid
        );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_preserves_the_aligned_extension_row()
    {
        _alignedRow.Should().NotBeNull();
        _alignedRow!.AlignedVisibleScalar.Should().Be("StoredVisible");
        _alignedRow.AlignedHiddenScalar.Should().Be("StoredHidden");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row
    : MssqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileCollectionAlignedExtensionParentRow> _parentRowsAfterPut = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Seed Parent")
        );
        await SeedAsync(seedBody, "mssql-profile-collection-aligned-create-denied-seed");

        var writeBody = MssqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new MssqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Rejected Parent",
                new MssqlProfileCollectionAlignedExtensionAlignedInput("RejectedVisible", "RejectedHidden")
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: false)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisibleAbsent)]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "mssql-profile-collection-aligned-create-denied-put"
        );
        _parentRowsAfterPut = await MssqlProfileCollectionAlignedExtensionSupport.ReadParentRowsAsync(
            Database,
            DocumentUuid
        );
        _alignedRowCount = await MssqlProfileCollectionAlignedExtensionSupport.CountAlignedRowsAsync(
            Database,
            DocumentUuid
        );
    }

    [Test]
    public void It_returns_profile_data_policy_failure() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateFailureProfileDataPolicy>(FormatResult(_putResult));

    [Test]
    public void It_does_not_create_the_aligned_extension_row() => _alignedRowCount.Should().Be(0);

    [Test]
    public void It_rolls_back_parent_row_changes() =>
        _parentRowsAfterPut.Should().ContainSingle().Which.ParentName.Should().Be("Seed Parent");
}
