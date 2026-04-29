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

file sealed class MssqlProfileNestedAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileNestedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed record MssqlProfileNestedChildInput(string ChildCode, string ChildValue);

internal sealed record MssqlProfileNestedParentInput(
    string ParentCode,
    string ParentName,
    IReadOnlyList<MssqlProfileNestedChildInput>? Children = null
);

internal sealed record MssqlProfileNestedRootExtChildInput(string RootExtChildCode, string RootExtChildValue);

internal sealed record MssqlProfileNestedRootExtInput(
    string RootExtVisibleScalar,
    string RootExtHiddenScalar,
    IReadOnlyList<MssqlProfileNestedRootExtChildInput>? Children = null
);

internal sealed record MssqlProfileNestedRequestParentItem(
    string ParentCode,
    int ArrayIndex,
    bool Creatable = true
);

internal sealed record MssqlProfileNestedRequestChildItem(
    string ParentCode,
    string ChildCode,
    int ParentArrayIndex,
    int ChildArrayIndex,
    bool Creatable = true
);

internal sealed record MssqlProfileNestedRequestRootExtChildItem(
    string RootExtChildCode,
    int ArrayIndex,
    bool Creatable = true
);

internal sealed record MssqlProfileNestedStoredParentRow(
    string ParentCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileNestedStoredChildRow(
    string ParentCode,
    string ChildCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileNestedStoredRootExtChildRow(
    string RootExtChildCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileNestedRequestRootExtScope(
    ProfileVisibilityKind Visibility,
    bool Creatable
);

internal sealed record MssqlProfileNestedStoredRootExtScope(
    ProfileVisibilityKind Visibility,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileNestedParentRow(
    long CollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string? ParentCode,
    string? ParentName
);

internal sealed record MssqlProfileNestedChildRow(
    long CollectionItemId,
    long ParentCollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string? ChildCode,
    string? ChildValue
);

internal sealed record MssqlProfileNestedRootExtRow(
    long DocumentId,
    string? RootExtVisibleScalar,
    string? RootExtHiddenScalar
);

internal sealed record MssqlProfileNestedRootExtChildRow(
    long CollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string? RootExtChildCode,
    string? RootExtChildValue
);

internal sealed class MssqlProfileNestedProjectionInvoker(
    ImmutableArray<MssqlProfileNestedStoredParentRow> storedParentRows,
    ImmutableArray<MssqlProfileNestedStoredChildRow> storedChildRows,
    ImmutableArray<MssqlProfileNestedStoredRootExtChildRow> storedRootExtChildRows,
    MssqlProfileNestedStoredRootExtScope? storedRootExtScope
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

        if (storedRootExtScope is not null)
        {
            storedScopeStates.Add(
                new StoredScopeState(
                    new ScopeInstanceAddress(MssqlProfileNestedSupport.RootExtScope, []),
                    storedRootExtScope.Visibility,
                    storedRootExtScope.HiddenMemberPaths
                )
            );
        }

        var visibleStoredRows = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>();
        foreach (var parentRow in storedParentRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    MssqlProfileNestedSupport.ParentRowAddress(parentRow.ParentCode),
                    parentRow.HiddenMemberPaths
                )
            );
        }

        foreach (var childRow in storedChildRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    MssqlProfileNestedSupport.ChildRowAddress(childRow.ParentCode, childRow.ChildCode),
                    childRow.HiddenMemberPaths
                )
            );
        }

        foreach (var rootExtChildRow in storedRootExtChildRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    MssqlProfileNestedSupport.RootExtChildRowAddress(rootExtChildRow.RootExtChildCode),
                    rootExtChildRow.HiddenMemberPaths
                )
            );
        }

        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates: storedScopeStates.ToImmutable(),
            VisibleStoredCollectionRows: visibleStoredRows.ToImmutable()
        );
    }
}

internal static class MssqlProfileNestedSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-nested-and-root-extension-children";

    public const string ParentScope = "$.parents[*]";
    public const string ChildScope = "$.parents[*].children[*]";
    public const string RootExtScope = "$._ext.root_ext";
    public const string RootExtChildScope = "$._ext.root_ext.root_ext_children[*]";

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
        IReadOnlyList<MssqlProfileNestedParentInput>? parents = null,
        MssqlProfileNestedRootExtInput? rootExt = null
    )
    {
        var body = new JsonObject { ["parentResourceId"] = parentResourceId };

        if (parents is not null)
        {
            JsonArray parentNodes = [];
            foreach (var parent in parents)
            {
                JsonObject parentNode = new()
                {
                    ["parentCode"] = parent.ParentCode,
                    ["parentName"] = parent.ParentName,
                };

                if (parent.Children is not null)
                {
                    JsonArray childNodes = [];
                    foreach (var child in parent.Children)
                    {
                        childNodes.Add(
                            new JsonObject
                            {
                                ["childCode"] = child.ChildCode,
                                ["childValue"] = child.ChildValue,
                            }
                        );
                    }
                    parentNode["children"] = childNodes;
                }

                parentNodes.Add(parentNode);
            }
            body["parents"] = parentNodes;
        }

        if (rootExt is not null)
        {
            JsonObject rootExtNode = new()
            {
                ["rootExtVisibleScalar"] = rootExt.RootExtVisibleScalar,
                ["rootExtHiddenScalar"] = rootExt.RootExtHiddenScalar,
            };

            if (rootExt.Children is not null)
            {
                JsonArray rootExtChildNodes = [];
                foreach (var child in rootExt.Children)
                {
                    rootExtChildNodes.Add(
                        new JsonObject
                        {
                            ["rootExtChildCode"] = child.RootExtChildCode,
                            ["rootExtChildValue"] = child.RootExtChildValue,
                        }
                    );
                }
                rootExtNode["root_ext_children"] = rootExtChildNodes;
            }

            body["_ext"] = new JsonObject { ["root_ext"] = rootExtNode };
        }

        return body;
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

    public static ImmutableArray<SemanticIdentityPart> ChildIdentity(string childCode) =>
        [new SemanticIdentityPart("childCode", JsonValue.Create(childCode), IsPresent: true)];

    public static ImmutableArray<SemanticIdentityPart> RootExtChildIdentity(string rootExtChildCode) =>
        [new SemanticIdentityPart("rootExtChildCode", JsonValue.Create(rootExtChildCode), IsPresent: true)];

    public static ScopeInstanceAddress ParentContainingScopeAddress(string parentCode) =>
        new(ParentScope, [new AncestorCollectionInstance(ParentScope, ParentIdentity(parentCode))]);

    public static CollectionRowAddress ParentRowAddress(string parentCode) =>
        new(ParentScope, new ScopeInstanceAddress("$", []), ParentIdentity(parentCode));

    public static CollectionRowAddress ChildRowAddress(string parentCode, string childCode) =>
        new(ChildScope, ParentContainingScopeAddress(parentCode), ChildIdentity(childCode));

    public static CollectionRowAddress RootExtChildRowAddress(string rootExtChildCode) =>
        new(
            RootExtChildScope,
            new ScopeInstanceAddress(RootExtScope, []),
            RootExtChildIdentity(rootExtChildCode)
        );

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<MssqlProfileNestedRequestParentItem> requestParentItems,
        IReadOnlyList<MssqlProfileNestedRequestChildItem>? requestChildItems = null,
        IReadOnlyList<MssqlProfileNestedRequestRootExtChildItem>? requestRootExtChildItems = null,
        MssqlProfileNestedRequestRootExtScope? requestRootExtScope = null,
        IReadOnlyList<MssqlProfileNestedStoredParentRow>? storedParentRows = null,
        IReadOnlyList<MssqlProfileNestedStoredChildRow>? storedChildRows = null,
        IReadOnlyList<MssqlProfileNestedStoredRootExtChildRow>? storedRootExtChildRows = null,
        MssqlProfileNestedStoredRootExtScope? storedRootExtScope = null,
        bool rootCreatable = true,
        string profileName = "nested-and-root-ext-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        var visibleRequestItems = ImmutableArray.CreateBuilder<VisibleRequestCollectionItem>();
        foreach (var item in requestParentItems)
        {
            visibleRequestItems.Add(
                new VisibleRequestCollectionItem(
                    ParentRowAddress(item.ParentCode),
                    item.Creatable,
                    $"$.parents[{item.ArrayIndex}]"
                )
            );
        }
        if (requestChildItems is not null)
        {
            foreach (var item in requestChildItems)
            {
                visibleRequestItems.Add(
                    new VisibleRequestCollectionItem(
                        ChildRowAddress(item.ParentCode, item.ChildCode),
                        item.Creatable,
                        $"$.parents[{item.ParentArrayIndex}].children[{item.ChildArrayIndex}]"
                    )
                );
            }
        }
        if (requestRootExtChildItems is not null)
        {
            foreach (var item in requestRootExtChildItems)
            {
                visibleRequestItems.Add(
                    new VisibleRequestCollectionItem(
                        RootExtChildRowAddress(item.RootExtChildCode),
                        item.Creatable,
                        $"$._ext.root_ext.root_ext_children[{item.ArrayIndex}]"
                    )
                );
            }
        }

        var requestScopeStates = ImmutableArray.CreateBuilder<RequestScopeState>();
        requestScopeStates.Add(
            new RequestScopeState(
                new ScopeInstanceAddress("$", []),
                ProfileVisibilityKind.VisiblePresent,
                rootCreatable
            )
        );
        if (requestRootExtScope is not null)
        {
            requestScopeStates.Add(
                new RequestScopeState(
                    new ScopeInstanceAddress(RootExtScope, []),
                    requestRootExtScope.Visibility,
                    requestRootExtScope.Creatable
                )
            );
        }

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates: requestScopeStates.ToImmutable(),
                VisibleRequestCollectionItems: visibleRequestItems.ToImmutable()
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlProfileNestedProjectionInvoker(
                [.. storedParentRows ?? []],
                [.. storedChildRows ?? []],
                [.. storedRootExtChildRows ?? []],
                storedRootExtScope
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
                    InstanceName: "MssqlProfileNested",
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
            UpdateCascadeHandler: new MssqlProfileNestedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileNestedAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
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
                    InstanceName: "MssqlProfileNested",
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
            UpdateCascadeHandler: new MssqlProfileNestedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileNestedAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<IReadOnlyList<MssqlProfileNestedParentRow>> ReadParentRowsAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
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
            new SqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new MssqlProfileNestedParentRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "ParentCode"),
                GetNullableString(row, "ParentName")
            ))
            .ToArray();
    }

    public static async Task<IReadOnlyList<MssqlProfileNestedChildRow>> ReadChildRowsAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                c.[CollectionItemId],
                c.[ParentCollectionItemId],
                c.[ParentResource_DocumentId],
                c.[Ordinal],
                c.[ChildCode],
                c.[ChildValue]
            FROM [edfi].[ParentResourceParentChildren] c
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = c.[ParentResource_DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid
            ORDER BY c.[ParentCollectionItemId], c.[Ordinal], c.[CollectionItemId];
            """,
            new SqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new MssqlProfileNestedChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "ChildCode"),
                GetNullableString(row, "ChildValue")
            ))
            .ToArray();
    }

    public static async Task<MssqlProfileNestedRootExtRow?> TryReadRootExtRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ext.[DocumentId],
                ext.[RootExtVisibleScalar],
                ext.[RootExtHiddenScalar]
            FROM [rootext].[ParentResourceExtension] ext
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ext.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Count == 0
            ? null
            : new MssqlProfileNestedRootExtRow(
                GetInt64(rows[0], "DocumentId"),
                GetNullableString(rows[0], "RootExtVisibleScalar"),
                GetNullableString(rows[0], "RootExtHiddenScalar")
            );
    }

    public static async Task<IReadOnlyList<MssqlProfileNestedRootExtChildRow>> ReadRootExtChildRowsAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ec.[CollectionItemId],
                ec.[ParentResource_DocumentId],
                ec.[Ordinal],
                ec.[RootExtChildCode],
                ec.[RootExtChildValue]
            FROM [rootext].[ParentResourceExtensionRootExtChildren] ec
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ec.[ParentResource_DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid
            ORDER BY ec.[Ordinal], ec.[CollectionItemId];
            """,
            new SqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new MssqlProfileNestedRootExtChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "RootExtChildCode"),
                GetNullableString(row, "RootExtChildValue")
            ))
            .ToArray();
    }

    private static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(row[columnName], CultureInfo.InvariantCulture);

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(row[columnName], CultureInfo.InvariantCulture);

    private static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row[columnName]?.ToString();
}

internal abstract class MssqlProfileNestedFixtureBase
{
    protected const int ParentResourceId = 9501;
    protected static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("ee020001-0000-0000-0000-000000000001")
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
            MssqlProfileNestedSupport.FixtureRelativePath
        );
        MappingSet = Fixture.MappingSet;
        Database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(Fixture.GeneratedDdl);
        ServiceProvider = MssqlProfileNestedSupport.CreateServiceProvider();
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
        MappingSet.WritePlansByResource[MssqlProfileNestedSupport.ParentResource];

    protected Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        BackendProfileWriteContext profileContext,
        string traceLabel
    ) =>
        MssqlProfileNestedSupport.ExecuteProfiledPutAsync(
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
        var seedResult = await MssqlProfileNestedSupport.SeedAsync(
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

    protected static string FormatResult(UpdateResult result) =>
        result switch
        {
            UpdateResult.UnknownFailure failure => failure.FailureMessage,
            _ => result.ToString() ?? result.GetType().Name,
        };
}

/// <summary>
/// Scenario 1: Nested ProfileVisibleRowUpdateWithHiddenRowPreservation. The profile
/// declares two visible nested children rows (under one matched parent) and the request
/// updates them; one additional hidden child row pre-exists in storage and must be
/// preserved unchanged. Pins the per-(scope, parent-instance) delete-by-absence
/// partitioning that prevents cross-parent contamination from leaking into the merge.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage
    : MssqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-VRU";
    private const string VisibleChildCodeA = "CHILD-V1";
    private const string VisibleChildCodeB = "CHILD-V2";
    private const string HiddenChildCode = "CHILD-H1";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentCode,
                    "Seed Parent",
                    Children:
                    [
                        new MssqlProfileNestedChildInput(VisibleChildCodeA, "SeedV1"),
                        new MssqlProfileNestedChildInput(VisibleChildCodeB, "SeedV2"),
                        new MssqlProfileNestedChildInput(HiddenChildCode, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "mssql-profile-nested-vru-seed");

        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentCode,
                    "Updated Parent",
                    Children:
                    [
                        new MssqlProfileNestedChildInput(VisibleChildCodeA, "UpdatedV1"),
                        new MssqlProfileNestedChildInput(VisibleChildCodeB, "UpdatedV2"),
                    ]
                ),
            ]
        );

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new MssqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems:
            [
                new MssqlProfileNestedRequestChildItem(ParentCode, VisibleChildCodeA, 0, 0),
                new MssqlProfileNestedRequestChildItem(ParentCode, VisibleChildCodeB, 0, 1),
            ],
            storedParentRows: [new MssqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new MssqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeA, []),
                new MssqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeB, []),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "mssql-profile-nested-vru-put");
        _childRows = await MssqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_keeps_three_child_rows_in_storage() => _childRows.Should().HaveCount(3);

    [Test]
    public void It_updates_the_visible_child_rows()
    {
        var byCode = _childRows.ToDictionary(r => r.ChildCode!);
        byCode[VisibleChildCodeA].ChildValue.Should().Be("UpdatedV1");
        byCode[VisibleChildCodeB].ChildValue.Should().Be("UpdatedV2");
    }

    [Test]
    public void It_preserves_the_hidden_sibling_row_unchanged()
    {
        var hiddenRow = _childRows.Single(r => r.ChildCode == HiddenChildCode);
        hiddenRow.ChildValue.Should().Be("SeedHidden");
    }
}

/// <summary>
/// Scenario 2: Nested ProfileVisibleRowDeleteWithHiddenRowPreservation. The request
/// omits the previously-visible nested child rows; the profile must therefore delete
/// them while preserving the hidden sibling row in storage.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_omitting_visible_children_with_a_hidden_sibling_in_storage
    : MssqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-VRD";
    private const string VisibleChildCodeA = "CHILD-V1";
    private const string VisibleChildCodeB = "CHILD-V2";
    private const string HiddenChildCode = "CHILD-H1";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentCode,
                    "Seed Parent",
                    Children:
                    [
                        new MssqlProfileNestedChildInput(VisibleChildCodeA, "SeedV1"),
                        new MssqlProfileNestedChildInput(VisibleChildCodeB, "SeedV2"),
                        new MssqlProfileNestedChildInput(HiddenChildCode, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "mssql-profile-nested-vrd-seed");

        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents: [new MssqlProfileNestedParentInput(ParentCode, "Updated Parent", Children: [])]
        );

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new MssqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [],
            storedParentRows: [new MssqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new MssqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeA, []),
                new MssqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeB, []),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "mssql-profile-nested-vrd-put");
        _childRows = await MssqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_deletes_the_visible_child_rows() =>
        _childRows.Select(r => r.ChildCode).Should().NotContain([VisibleChildCodeA, VisibleChildCodeB]);

    [Test]
    public void It_preserves_the_hidden_sibling_row()
    {
        _childRows
            .Should()
            .ContainSingle(r => r.ChildCode == HiddenChildCode)
            .Which.ChildValue.Should()
            .Be("SeedHidden");
    }
}

/// <summary>
/// Scenario 3: Root-extension child collection variant. The request modifies rows under
/// $._ext.root_ext.root_ext_children[*]; the profile keeps the root extension visible and
/// publishes the visible children. CP4 opens the slice gate so this entire shape now
/// reaches the synthesizer.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_updating_root_extension_child_collection
    : MssqlProfileNestedFixtureBase
{
    private const string RootExtChildCodeA = "REC-V1";
    private const string RootExtChildCodeB = "REC-V2";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileNestedRootExtChildRow> _rootExtChildRows = null!;
    private MssqlProfileNestedRootExtRow? _rootExtRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            rootExt: new MssqlProfileNestedRootExtInput(
                "SeedVisible",
                "SeedHidden",
                Children:
                [
                    new MssqlProfileNestedRootExtChildInput(RootExtChildCodeA, "SeedRECV1"),
                    new MssqlProfileNestedRootExtChildInput(RootExtChildCodeB, "SeedRECV2"),
                ]
            )
        );
        await SeedAsync(seedBody, "mssql-profile-nested-rec-update-seed");

        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            rootExt: new MssqlProfileNestedRootExtInput(
                "UpdatedVisible",
                "UpdatedHidden",
                Children:
                [
                    new MssqlProfileNestedRootExtChildInput(RootExtChildCodeA, "UpdatedRECV1"),
                    new MssqlProfileNestedRootExtChildInput(RootExtChildCodeB, "UpdatedRECV2"),
                ]
            )
        );

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [],
            requestRootExtChildItems:
            [
                new MssqlProfileNestedRequestRootExtChildItem(RootExtChildCodeA, ArrayIndex: 0),
                new MssqlProfileNestedRequestRootExtChildItem(RootExtChildCodeB, ArrayIndex: 1),
            ],
            requestRootExtScope: new MssqlProfileNestedRequestRootExtScope(
                ProfileVisibilityKind.VisiblePresent,
                Creatable: true
            ),
            storedRootExtChildRows:
            [
                new MssqlProfileNestedStoredRootExtChildRow(RootExtChildCodeA, []),
                new MssqlProfileNestedStoredRootExtChildRow(RootExtChildCodeB, []),
            ],
            storedRootExtScope: new MssqlProfileNestedStoredRootExtScope(
                ProfileVisibilityKind.VisiblePresent,
                []
            )
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "mssql-profile-nested-rec-update-put"
        );
        _rootExtChildRows = await MssqlProfileNestedSupport.ReadRootExtChildRowsAsync(Database, DocumentUuid);
        _rootExtRow = await MssqlProfileNestedSupport.TryReadRootExtRowAsync(Database, DocumentUuid);
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_keeps_two_root_extension_child_rows() => _rootExtChildRows.Should().HaveCount(2);

    [Test]
    public void It_updates_the_root_extension_child_values()
    {
        var byCode = _rootExtChildRows.ToDictionary(r => r.RootExtChildCode!);
        byCode[RootExtChildCodeA].RootExtChildValue.Should().Be("UpdatedRECV1");
        byCode[RootExtChildCodeB].RootExtChildValue.Should().Be("UpdatedRECV2");
    }

    [Test]
    public void It_updates_the_root_extension_scalars()
    {
        _rootExtRow.Should().NotBeNull();
        _rootExtRow!.RootExtVisibleScalar.Should().Be("UpdatedVisible");
        _rootExtRow.RootExtHiddenScalar.Should().Be("UpdatedHidden");
    }
}

/// <summary>
/// Scenario 4: ProfileHiddenExtensionChildCollectionPreservation. When the root-extension
/// scope is hidden in the profile, all of its children (and the extension scope itself)
/// must be preserved unchanged from storage; the profile request supplies no payload for
/// the extension subtree. Pins WalkMode.Preserve recursion for hidden separate-table
/// scopes with collection children.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children
    : MssqlProfileNestedFixtureBase
{
    private const string RootExtChildCodeA = "REC-H1";
    private const string RootExtChildCodeB = "REC-H2";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileNestedRootExtChildRow> _rootExtChildRows = null!;
    private MssqlProfileNestedRootExtRow? _rootExtRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            rootExt: new MssqlProfileNestedRootExtInput(
                "SeedVisible",
                "SeedHidden",
                Children:
                [
                    new MssqlProfileNestedRootExtChildInput(RootExtChildCodeA, "SeedRECH1"),
                    new MssqlProfileNestedRootExtChildInput(RootExtChildCodeB, "SeedRECH2"),
                ]
            )
        );
        await SeedAsync(seedBody, "mssql-profile-nested-hidden-rec-seed");

        // Request body keeps only the root document. Hidden extension scope means the
        // synthesizer never sees a payload for the extension and must not touch storage.
        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(ParentResourceId);

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [],
            requestRootExtScope: new MssqlProfileNestedRequestRootExtScope(
                ProfileVisibilityKind.Hidden,
                Creatable: false
            ),
            storedRootExtChildRows:
            [
                new MssqlProfileNestedStoredRootExtChildRow(RootExtChildCodeA, []),
                new MssqlProfileNestedStoredRootExtChildRow(RootExtChildCodeB, []),
            ],
            storedRootExtScope: new MssqlProfileNestedStoredRootExtScope(ProfileVisibilityKind.Hidden, [])
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "mssql-profile-nested-hidden-rec-put"
        );
        _rootExtChildRows = await MssqlProfileNestedSupport.ReadRootExtChildRowsAsync(Database, DocumentUuid);
        _rootExtRow = await MssqlProfileNestedSupport.TryReadRootExtRowAsync(Database, DocumentUuid);
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_preserves_both_root_extension_child_rows()
    {
        _rootExtChildRows.Should().HaveCount(2);
        var byCode = _rootExtChildRows.ToDictionary(r => r.RootExtChildCode!);
        byCode[RootExtChildCodeA].RootExtChildValue.Should().Be("SeedRECH1");
        byCode[RootExtChildCodeB].RootExtChildValue.Should().Be("SeedRECH2");
    }

    [Test]
    public void It_preserves_the_root_extension_row_scalars()
    {
        _rootExtRow.Should().NotBeNull();
        _rootExtRow!.RootExtVisibleScalar.Should().Be("SeedVisible");
        _rootExtRow.RootExtHiddenScalar.Should().Be("SeedHidden");
    }
}

/// <summary>
/// Scenario 5: Three-level update-allowed/create-denied chain. CP2 had synthesizer-level
/// coverage; this is the HTTP-level companion. The profile keeps parents and their
/// children visible, but with creatable=false on the children scope. Storage already has
/// the parent + child; the request updates the child's value (allowed under matched
/// update). A separate parent with NO stored children is sent with a new child candidate
/// and must be rejected with a creatability rejection (synthesizer returns rejection).
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_with_creatable_false_on_children_rejects_new_children
    : MssqlProfileNestedFixtureBase
{
    private const string ParentACode = "PARENT-3L-A";
    private const string ChildCodeA = "CHILD-3L-A";

    private UpdateResult _putResult = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        // Storage has parent A with NO stored children.
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents: [new MssqlProfileNestedParentInput(ParentACode, "Seed", Children: [])]
        );
        await SeedAsync(seedBody, "mssql-profile-nested-3l-seed");

        // Request adds a new child under parent A; profile makes the child scope visible
        // but NOT creatable, so the planner must reject the insert.
        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentACode,
                    "Updated",
                    Children: [new MssqlProfileNestedChildInput(ChildCodeA, "NewValue")]
                ),
            ]
        );

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new MssqlProfileNestedRequestParentItem(ParentACode, ArrayIndex: 0)],
            requestChildItems:
            [
                new MssqlProfileNestedRequestChildItem(
                    ParentACode,
                    ChildCodeA,
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    Creatable: false
                ),
            ],
            storedParentRows: [new MssqlProfileNestedStoredParentRow(ParentACode, [])]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "mssql-profile-nested-3l-put");
    }

    [Test]
    public void It_returns_a_profile_data_policy_failure() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateFailureProfileDataPolicy>(FormatResult(_putResult));
}

/// <summary>
/// Scenario 6: Nested delete-all-visible-while-hidden-rows-remain. The request omits all
/// nested children for an existing parent; storage has multiple visible children plus
/// a hidden child. The merge must delete every visible child while preserving the hidden
/// child (ordinal recomputed for the surviving row).
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_omitting_all_visible_children_with_hidden_remaining
    : MssqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-DAH";
    private const string VisibleChildA = "CHILD-DAH-V1";
    private const string VisibleChildB = "CHILD-DAH-V2";
    private const string HiddenChild = "CHILD-DAH-H";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentCode,
                    "Seed",
                    Children:
                    [
                        new MssqlProfileNestedChildInput(VisibleChildA, "SeedV1"),
                        new MssqlProfileNestedChildInput(VisibleChildB, "SeedV2"),
                        new MssqlProfileNestedChildInput(HiddenChild, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "mssql-profile-nested-dah-seed");

        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents: [new MssqlProfileNestedParentInput(ParentCode, "Updated", Children: [])]
        );

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new MssqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [],
            storedParentRows: [new MssqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new MssqlProfileNestedStoredChildRow(ParentCode, VisibleChildA, []),
                new MssqlProfileNestedStoredChildRow(ParentCode, VisibleChildB, []),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "mssql-profile-nested-dah-put");
        _childRows = await MssqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_deletes_both_visible_child_rows() =>
        _childRows.Select(r => r.ChildCode).Should().NotContain([VisibleChildA, VisibleChildB]);

    [Test]
    public void It_preserves_only_the_hidden_child_row()
    {
        _childRows.Should().ContainSingle().Which.ChildCode.Should().Be(HiddenChild);
        _childRows.Single().ChildValue.Should().Be("SeedHidden");
    }
}

/// <summary>
/// Scenario 7: Nested hidden-binding preservation. The visible nested row's
/// non-identity scalar is published in the profile as a hidden member path. Storage holds
/// the prior value; the request submits a different value at the visible identity scalar
/// path AND at the hidden scalar path, but the merged row must preserve the stored value
/// at the hidden path while overlaying the visible scalars from the request.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_a_ProfileNested_put_request_with_a_hidden_member_path_on_a_visible_child
    : MssqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-HMP";
    private const string ChildCode = "CHILD-HMP";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<MssqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        // Stored child carries the hidden value; the matched-row overlay must preserve
        // it on the merge regardless of what the request sends.
        var seedBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentCode,
                    "Seed",
                    Children: [new MssqlProfileNestedChildInput(ChildCode, "StoredHiddenValue")]
                ),
            ]
        );
        await SeedAsync(seedBody, "mssql-profile-nested-hmp-seed");

        // Request tries to overwrite the hidden scalar; the matched-row overlay must
        // ignore the request value at that path and copy the stored value forward.
        var writeBody = MssqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new MssqlProfileNestedParentInput(
                    ParentCode,
                    "Updated",
                    Children: [new MssqlProfileNestedChildInput(ChildCode, "RequestAttemptedValue")]
                ),
            ]
        );

        var profileContext = MssqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new MssqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [new MssqlProfileNestedRequestChildItem(ParentCode, ChildCode, 0, 0)],
            storedParentRows: [new MssqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new MssqlProfileNestedStoredChildRow(
                    ParentCode,
                    ChildCode,
                    HiddenMemberPaths: ["childValue"]
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "mssql-profile-nested-hmp-put");
        _childRows = await MssqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_preserves_the_stored_value_at_the_hidden_path()
    {
        _childRows.Should().ContainSingle();
        _childRows.Single().ChildCode.Should().Be(ChildCode);
        _childRows.Single().ChildValue.Should().Be("StoredHiddenValue");
    }
}
