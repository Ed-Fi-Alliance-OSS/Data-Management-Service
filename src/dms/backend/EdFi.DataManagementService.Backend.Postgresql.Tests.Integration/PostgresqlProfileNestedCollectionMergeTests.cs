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
using EdFi.DataManagementService.Backend.Tests.Common;
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

file sealed class PostgresqlProfileNestedNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostgresqlProfileNestedAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostgresqlProfileNestedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed record PostgresqlProfileNestedChildInput(string ChildCode, string ChildValue);

internal sealed record PostgresqlProfileNestedParentInput(
    string ParentCode,
    string ParentName,
    IReadOnlyList<PostgresqlProfileNestedChildInput>? Children = null
);

internal sealed record PostgresqlProfileNestedRootExtChildInput(
    string RootExtChildCode,
    string RootExtChildValue
);

internal sealed record PostgresqlProfileNestedRootExtInput(
    string RootExtVisibleScalar,
    string RootExtHiddenScalar,
    IReadOnlyList<PostgresqlProfileNestedRootExtChildInput>? Children = null
);

internal sealed record PostgresqlProfileNestedRequestParentItem(
    string ParentCode,
    int ArrayIndex,
    bool Creatable = true
);

internal sealed record PostgresqlProfileNestedRequestChildItem(
    string ParentCode,
    string ChildCode,
    int ParentArrayIndex,
    int ChildArrayIndex,
    bool Creatable = true
);

internal sealed record PostgresqlProfileNestedRequestRootExtChildItem(
    string RootExtChildCode,
    int ArrayIndex,
    bool Creatable = true
);

internal sealed record PostgresqlProfileNestedStoredParentRow(
    string ParentCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileNestedStoredChildRow(
    string ParentCode,
    string ChildCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileNestedStoredRootExtChildRow(
    string RootExtChildCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileNestedRequestRootExtScope(
    ProfileVisibilityKind Visibility,
    bool Creatable
);

internal sealed record PostgresqlProfileNestedStoredRootExtScope(
    ProfileVisibilityKind Visibility,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileNestedParentRow(
    long CollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string? ParentCode,
    string? ParentName
);

internal sealed record PostgresqlProfileNestedChildRow(
    long CollectionItemId,
    long ParentCollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string? ChildCode,
    string? ChildValue
);

internal sealed record PostgresqlProfileNestedRootExtRow(
    long DocumentId,
    string? RootExtVisibleScalar,
    string? RootExtHiddenScalar
);

internal sealed record PostgresqlProfileNestedRootExtChildRow(
    long CollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string? RootExtChildCode,
    string? RootExtChildValue
);

internal sealed class PostgresqlProfileNestedProjectionInvoker(
    ImmutableArray<PostgresqlProfileNestedStoredParentRow> storedParentRows,
    ImmutableArray<PostgresqlProfileNestedStoredChildRow> storedChildRows,
    ImmutableArray<PostgresqlProfileNestedStoredRootExtChildRow> storedRootExtChildRows,
    PostgresqlProfileNestedStoredRootExtScope? storedRootExtScope
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
                    new ScopeInstanceAddress(PostgresqlProfileNestedSupport.RootExtScope, []),
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
                    PostgresqlProfileNestedSupport.ParentRowAddress(parentRow.ParentCode),
                    parentRow.HiddenMemberPaths
                )
            );
        }

        foreach (var childRow in storedChildRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    PostgresqlProfileNestedSupport.ChildRowAddress(childRow.ParentCode, childRow.ChildCode),
                    childRow.HiddenMemberPaths
                )
            );
        }

        foreach (var rootExtChildRow in storedRootExtChildRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    PostgresqlProfileNestedSupport.RootExtChildRowAddress(rootExtChildRow.RootExtChildCode),
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

internal static class PostgresqlProfileNestedSupport
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
        services.AddSingleton<IHostApplicationLifetime, PostgresqlProfileNestedNoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static JsonNode CreateParentResourceBody(
        int parentResourceId,
        IReadOnlyList<PostgresqlProfileNestedParentInput>? parents = null,
        PostgresqlProfileNestedRootExtInput? rootExt = null
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
        IReadOnlyList<PostgresqlProfileNestedRequestParentItem> requestParentItems,
        IReadOnlyList<PostgresqlProfileNestedRequestChildItem>? requestChildItems = null,
        IReadOnlyList<PostgresqlProfileNestedRequestRootExtChildItem>? requestRootExtChildItems = null,
        PostgresqlProfileNestedRequestRootExtScope? requestRootExtScope = null,
        IReadOnlyList<PostgresqlProfileNestedStoredParentRow>? storedParentRows = null,
        IReadOnlyList<PostgresqlProfileNestedStoredChildRow>? storedChildRows = null,
        IReadOnlyList<PostgresqlProfileNestedStoredRootExtChildRow>? storedRootExtChildRows = null,
        PostgresqlProfileNestedStoredRootExtScope? storedRootExtScope = null,
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
            StoredStateProjectionInvoker: new PostgresqlProfileNestedProjectionInvoker(
                [.. storedParentRows ?? []],
                [.. storedChildRows ?? []],
                [.. storedRootExtChildRows ?? []],
                storedRootExtScope
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "PostgresqlProfileNested",
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
            UpdateCascadeHandler: new PostgresqlProfileNestedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileNestedAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "PostgresqlProfileNested",
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
            UpdateCascadeHandler: new PostgresqlProfileNestedNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileNestedAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<IReadOnlyList<PostgresqlProfileNestedParentRow>> ReadParentRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                p."CollectionItemId",
                p."ParentResource_DocumentId",
                p."Ordinal",
                p."ParentCode",
                p."ParentName"
            FROM "edfi"."ParentResourceParent" p
            INNER JOIN "dms"."Document" d ON d."DocumentId" = p."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid
            ORDER BY p."Ordinal", p."CollectionItemId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new PostgresqlProfileNestedParentRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "ParentCode"),
                GetNullableString(row, "ParentName")
            ))
            .ToArray();
    }

    public static async Task<IReadOnlyList<PostgresqlProfileNestedChildRow>> ReadChildRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                c."CollectionItemId",
                c."ParentCollectionItemId",
                c."ParentResource_DocumentId",
                c."Ordinal",
                c."ChildCode",
                c."ChildValue"
            FROM "edfi"."ParentResourceParentChildren" c
            INNER JOIN "dms"."Document" d ON d."DocumentId" = c."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid
            ORDER BY c."ParentCollectionItemId", c."Ordinal", c."CollectionItemId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new PostgresqlProfileNestedChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "ChildCode"),
                GetNullableString(row, "ChildValue")
            ))
            .ToArray();
    }

    public static async Task<PostgresqlProfileNestedRootExtRow?> TryReadRootExtRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ext."DocumentId",
                ext."RootExtVisibleScalar",
                ext."RootExtHiddenScalar"
            FROM "rootext"."ParentResourceExtension" ext
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ext."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Count == 0
            ? null
            : new PostgresqlProfileNestedRootExtRow(
                GetInt64(rows[0], "DocumentId"),
                GetNullableString(rows[0], "RootExtVisibleScalar"),
                GetNullableString(rows[0], "RootExtHiddenScalar")
            );
    }

    public static async Task<IReadOnlyList<PostgresqlProfileNestedRootExtChildRow>> ReadRootExtChildRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ec."CollectionItemId",
                ec."ParentResource_DocumentId",
                ec."Ordinal",
                ec."RootExtChildCode",
                ec."RootExtChildValue"
            FROM "rootext"."ParentResourceExtensionRootExtChildren" ec
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ec."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid
            ORDER BY ec."Ordinal", ec."CollectionItemId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new PostgresqlProfileNestedRootExtChildRow(
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

internal abstract class PostgresqlProfileNestedFixtureBase
{
    protected const int ParentResourceId = 9401;
    protected static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("ee010001-0000-0000-0000-000000000001")
    );

    protected PostgresqlGeneratedDdlFixture Fixture = null!;
    protected MappingSet MappingSet = null!;
    protected PostgresqlGeneratedDdlTestDatabase Database = null!;
    protected ServiceProvider ServiceProvider = null!;

    [OneTimeSetUp]
    public async Task BaseOneTimeSetUp()
    {
        Fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileNestedSupport.FixtureRelativePath
        );
        MappingSet = Fixture.MappingSet;
        Database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(Fixture.GeneratedDdl);
        ServiceProvider = PostgresqlProfileNestedSupport.CreateServiceProvider();
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
        MappingSet.WritePlansByResource[PostgresqlProfileNestedSupport.ParentResource];

    protected Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        BackendProfileWriteContext profileContext,
        string traceLabel
    ) =>
        PostgresqlProfileNestedSupport.ExecuteProfiledPutAsync(
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
        var seedResult = await PostgresqlProfileNestedSupport.SeedAsync(
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage
    : PostgresqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-VRU";
    private const string VisibleChildCodeA = "CHILD-V1";
    private const string VisibleChildCodeB = "CHILD-V2";
    private const string HiddenChildCode = "CHILD-H1";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentCode,
                    "Seed Parent",
                    Children:
                    [
                        new PostgresqlProfileNestedChildInput(VisibleChildCodeA, "SeedV1"),
                        new PostgresqlProfileNestedChildInput(VisibleChildCodeB, "SeedV2"),
                        new PostgresqlProfileNestedChildInput(HiddenChildCode, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-vru-seed");

        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentCode,
                    "Updated Parent",
                    Children:
                    [
                        new PostgresqlProfileNestedChildInput(VisibleChildCodeA, "UpdatedV1"),
                        new PostgresqlProfileNestedChildInput(VisibleChildCodeB, "UpdatedV2"),
                    ]
                ),
            ]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new PostgresqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems:
            [
                new PostgresqlProfileNestedRequestChildItem(ParentCode, VisibleChildCodeA, 0, 0),
                new PostgresqlProfileNestedRequestChildItem(ParentCode, VisibleChildCodeB, 0, 1),
            ],
            storedParentRows: [new PostgresqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new PostgresqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeA, []),
                new PostgresqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeB, []),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "pgsql-profile-nested-vru-put");
        _childRows = await PostgresqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_omitting_visible_children_with_a_hidden_sibling_in_storage
    : PostgresqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-VRD";
    private const string VisibleChildCodeA = "CHILD-V1";
    private const string VisibleChildCodeB = "CHILD-V2";
    private const string HiddenChildCode = "CHILD-H1";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentCode,
                    "Seed Parent",
                    Children:
                    [
                        new PostgresqlProfileNestedChildInput(VisibleChildCodeA, "SeedV1"),
                        new PostgresqlProfileNestedChildInput(VisibleChildCodeB, "SeedV2"),
                        new PostgresqlProfileNestedChildInput(HiddenChildCode, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-vrd-seed");

        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents: [new PostgresqlProfileNestedParentInput(ParentCode, "Updated Parent", Children: [])]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new PostgresqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [],
            storedParentRows: [new PostgresqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new PostgresqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeA, []),
                new PostgresqlProfileNestedStoredChildRow(ParentCode, VisibleChildCodeB, []),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "pgsql-profile-nested-vrd-put");
        _childRows = await PostgresqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_updating_root_extension_child_collection
    : PostgresqlProfileNestedFixtureBase
{
    private const string RootExtChildCodeA = "REC-V1";
    private const string RootExtChildCodeB = "REC-V2";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileNestedRootExtChildRow> _rootExtChildRows = null!;
    private PostgresqlProfileNestedRootExtRow? _rootExtRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            rootExt: new PostgresqlProfileNestedRootExtInput(
                "SeedVisible",
                "SeedHidden",
                Children:
                [
                    new PostgresqlProfileNestedRootExtChildInput(RootExtChildCodeA, "SeedRECV1"),
                    new PostgresqlProfileNestedRootExtChildInput(RootExtChildCodeB, "SeedRECV2"),
                ]
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-rec-update-seed");

        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            rootExt: new PostgresqlProfileNestedRootExtInput(
                "UpdatedVisible",
                "UpdatedHidden",
                Children:
                [
                    new PostgresqlProfileNestedRootExtChildInput(RootExtChildCodeA, "UpdatedRECV1"),
                    new PostgresqlProfileNestedRootExtChildInput(RootExtChildCodeB, "UpdatedRECV2"),
                ]
            )
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [],
            requestRootExtChildItems:
            [
                new PostgresqlProfileNestedRequestRootExtChildItem(RootExtChildCodeA, ArrayIndex: 0),
                new PostgresqlProfileNestedRequestRootExtChildItem(RootExtChildCodeB, ArrayIndex: 1),
            ],
            requestRootExtScope: new PostgresqlProfileNestedRequestRootExtScope(
                ProfileVisibilityKind.VisiblePresent,
                Creatable: true
            ),
            storedRootExtChildRows:
            [
                new PostgresqlProfileNestedStoredRootExtChildRow(RootExtChildCodeA, []),
                new PostgresqlProfileNestedStoredRootExtChildRow(RootExtChildCodeB, []),
            ],
            storedRootExtScope: new PostgresqlProfileNestedStoredRootExtScope(
                ProfileVisibilityKind.VisiblePresent,
                []
            )
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "pgsql-profile-nested-rec-update-put"
        );
        _rootExtChildRows = await PostgresqlProfileNestedSupport.ReadRootExtChildRowsAsync(
            Database,
            DocumentUuid
        );
        _rootExtRow = await PostgresqlProfileNestedSupport.TryReadRootExtRowAsync(Database, DocumentUuid);
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children
    : PostgresqlProfileNestedFixtureBase
{
    private const string RootExtChildCodeA = "REC-H1";
    private const string RootExtChildCodeB = "REC-H2";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileNestedRootExtChildRow> _rootExtChildRows = null!;
    private PostgresqlProfileNestedRootExtRow? _rootExtRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            rootExt: new PostgresqlProfileNestedRootExtInput(
                "SeedVisible",
                "SeedHidden",
                Children:
                [
                    new PostgresqlProfileNestedRootExtChildInput(RootExtChildCodeA, "SeedRECH1"),
                    new PostgresqlProfileNestedRootExtChildInput(RootExtChildCodeB, "SeedRECH2"),
                ]
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-hidden-rec-seed");

        // Request body keeps only the root document. Hidden extension scope means the
        // synthesizer never sees a payload for the extension and must not touch storage.
        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(ParentResourceId);

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [],
            requestRootExtScope: new PostgresqlProfileNestedRequestRootExtScope(
                ProfileVisibilityKind.Hidden,
                Creatable: false
            ),
            storedRootExtChildRows:
            [
                new PostgresqlProfileNestedStoredRootExtChildRow(RootExtChildCodeA, []),
                new PostgresqlProfileNestedStoredRootExtChildRow(RootExtChildCodeB, []),
            ],
            storedRootExtScope: new PostgresqlProfileNestedStoredRootExtScope(
                ProfileVisibilityKind.Hidden,
                []
            )
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "pgsql-profile-nested-hidden-rec-put"
        );
        _rootExtChildRows = await PostgresqlProfileNestedSupport.ReadRootExtChildRowsAsync(
            Database,
            DocumentUuid
        );
        _rootExtRow = await PostgresqlProfileNestedSupport.TryReadRootExtRowAsync(Database, DocumentUuid);
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_with_creatable_false_on_children_rejects_new_children
    : PostgresqlProfileNestedFixtureBase
{
    private const string ParentACode = "PARENT-3L-A";
    private const string ChildCodeA = "CHILD-3L-A";

    private UpdateResult _putResult = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        // Storage has parent A with NO stored children.
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents: [new PostgresqlProfileNestedParentInput(ParentACode, "Seed", Children: [])]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-3l-seed");

        // Request adds a new child under parent A; profile makes the child scope visible
        // but NOT creatable, so the planner must reject the insert.
        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentACode,
                    "Updated",
                    Children: [new PostgresqlProfileNestedChildInput(ChildCodeA, "NewValue")]
                ),
            ]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new PostgresqlProfileNestedRequestParentItem(ParentACode, ArrayIndex: 0)],
            requestChildItems:
            [
                new PostgresqlProfileNestedRequestChildItem(
                    ParentACode,
                    ChildCodeA,
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    Creatable: false
                ),
            ],
            storedParentRows: [new PostgresqlProfileNestedStoredParentRow(ParentACode, [])]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "pgsql-profile-nested-3l-put");
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_omitting_all_visible_children_with_hidden_remaining
    : PostgresqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-DAH";
    private const string VisibleChildA = "CHILD-DAH-V1";
    private const string VisibleChildB = "CHILD-DAH-V2";
    private const string HiddenChild = "CHILD-DAH-H";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentCode,
                    "Seed",
                    Children:
                    [
                        new PostgresqlProfileNestedChildInput(VisibleChildA, "SeedV1"),
                        new PostgresqlProfileNestedChildInput(VisibleChildB, "SeedV2"),
                        new PostgresqlProfileNestedChildInput(HiddenChild, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-dah-seed");

        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents: [new PostgresqlProfileNestedParentInput(ParentCode, "Updated", Children: [])]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new PostgresqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [],
            storedParentRows: [new PostgresqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new PostgresqlProfileNestedStoredChildRow(ParentCode, VisibleChildA, []),
                new PostgresqlProfileNestedStoredChildRow(ParentCode, VisibleChildB, []),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "pgsql-profile-nested-dah-put");
        _childRows = await PostgresqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileNested_put_request_with_a_hidden_member_path_on_a_visible_child
    : PostgresqlProfileNestedFixtureBase
{
    private const string ParentCode = "PARENT-HMP";
    private const string ChildCode = "CHILD-HMP";

    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileNestedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        // Stored child carries the hidden value; the matched-row overlay must preserve
        // it on the merge regardless of what the request sends.
        var seedBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentCode,
                    "Seed",
                    Children: [new PostgresqlProfileNestedChildInput(ChildCode, "StoredHiddenValue")]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-hmp-seed");

        // Request tries to overwrite the hidden scalar; the matched-row overlay must
        // ignore the request value at that path and copy the stored value forward.
        var writeBody = PostgresqlProfileNestedSupport.CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new PostgresqlProfileNestedParentInput(
                    ParentCode,
                    "Updated",
                    Children: [new PostgresqlProfileNestedChildInput(ChildCode, "RequestAttemptedValue")]
                ),
            ]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new PostgresqlProfileNestedRequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [new PostgresqlProfileNestedRequestChildItem(ParentCode, ChildCode, 0, 0)],
            storedParentRows: [new PostgresqlProfileNestedStoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new PostgresqlProfileNestedStoredChildRow(
                    ParentCode,
                    ChildCode,
                    HiddenMemberPaths: ["childValue"]
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(writeBody, profileContext, "pgsql-profile-nested-hmp-put");
        _childRows = await PostgresqlProfileNestedSupport.ReadChildRowsAsync(Database, DocumentUuid);
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
