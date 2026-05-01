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

file sealed class PostgresqlProfileCollectionAlignedExtensionNoOpHostApplicationLifetime
    : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostgresqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostgresqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed class PostgresqlProfileCollectionAlignedExtensionProjectionInvoker(
    ImmutableArray<PostgresqlProfileCollectionAlignedExtensionStoredParentRow> storedParentRows,
    ImmutableArray<PostgresqlProfileCollectionAlignedExtensionStoredAlignedScope> storedAlignedScopes,
    ImmutableArray<PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow> storedAlignedChildRows,
    ImmutableArray<PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow> storedExtensionChildRows
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
                PostgresqlProfileCollectionAlignedExtensionSupport.AlignedScopeAddress(scope.ParentCode),
                scope.Visibility,
                scope.HiddenMemberPaths
            ))
        );

        var visibleStoredRows = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>();
        visibleStoredRows.AddRange(
            storedParentRows.Select(row => new VisibleStoredCollectionRow(
                PostgresqlProfileCollectionAlignedExtensionSupport.ParentCollectionRowAddress(row.ParentCode),
                row.HiddenMemberPaths
            ))
        );
        visibleStoredRows.AddRange(
            storedAlignedChildRows.Select(row => new VisibleStoredCollectionRow(
                PostgresqlProfileCollectionAlignedExtensionSupport.AlignedChildCollectionRowAddress(
                    row.ParentCode,
                    row.ChildCode
                ),
                row.HiddenMemberPaths
            ))
        );
        visibleStoredRows.AddRange(
            storedExtensionChildRows.Select(row => new VisibleStoredCollectionRow(
                PostgresqlProfileCollectionAlignedExtensionSupport.ExtensionChildCollectionRowAddress(
                    row.ParentCode,
                    row.ChildCode,
                    row.ExtensionChildCode
                ),
                row.HiddenMemberPaths
            ))
        );

        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates: storedScopeStates.ToImmutable(),
            VisibleStoredCollectionRows: visibleStoredRows.ToImmutable()
        );
    }
}

internal sealed record PostgresqlProfileCollectionAlignedExtensionParentInput(
    string ParentCode,
    string ParentName,
    PostgresqlProfileCollectionAlignedExtensionAlignedInput? Aligned = null
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionAlignedInput(
    string AlignedVisibleScalar,
    string AlignedHiddenScalar,
    IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildInput>? Children = null
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
    string ChildCode,
    string? ChildValue,
    IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildInput>? ExtensionChildren = null
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
    string ExtensionChildCode,
    string? ExtensionChildValue
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionRequestParentItem(
    string ParentCode,
    int ArrayIndex,
    bool Creatable = true
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionRequestAlignedScope(
    string ParentCode,
    ProfileVisibilityKind Visibility,
    bool Creatable
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
    string ParentCode,
    string ChildCode,
    int ParentArrayIndex,
    int ChildArrayIndex,
    bool Creatable = true
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
    string ParentCode,
    string ChildCode,
    string ExtensionChildCode,
    int ParentArrayIndex,
    int ChildArrayIndex,
    int ExtensionChildArrayIndex,
    bool Creatable = true
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionStoredParentRow(
    string ParentCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionStoredAlignedScope(
    string ParentCode,
    ProfileVisibilityKind Visibility,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
    string ParentCode,
    string ChildCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
    string ParentCode,
    string ChildCode,
    string ExtensionChildCode,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionParentRow(
    long CollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string ParentCode,
    string ParentName
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionAlignedRow(
    long BaseCollectionItemId,
    long ParentResourceDocumentId,
    string? AlignedVisibleScalar,
    string? AlignedHiddenScalar
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionAlignedChildRow(
    long CollectionItemId,
    long BaseCollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string ChildCode,
    string? ChildValue
);

internal sealed record PostgresqlProfileCollectionAlignedExtensionExtensionChildRow(
    long CollectionItemId,
    long ParentCollectionItemId,
    long ParentResourceDocumentId,
    int Ordinal,
    string ExtensionChildCode,
    string? ExtensionChildValue
);

internal static class PostgresqlProfileCollectionAlignedExtensionSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension";

    public const string ParentScope = "$.parents[*]";
    public const string AlignedScope = "$.parents[*]._ext.aligned";
    public const string AlignedChildScope = "$.parents[*]._ext.aligned.children[*]";
    public const string ExtensionChildScope = "$.parents[*]._ext.aligned.children[*].extensionChildren[*]";

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
        services.AddSingleton<
            IHostApplicationLifetime,
            PostgresqlProfileCollectionAlignedExtensionNoOpHostApplicationLifetime
        >();
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
        params PostgresqlProfileCollectionAlignedExtensionParentInput[] parents
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
                JsonObject alignedNode = new()
                {
                    ["alignedVisibleScalar"] = parent.Aligned.AlignedVisibleScalar,
                    ["alignedHiddenScalar"] = parent.Aligned.AlignedHiddenScalar,
                };

                if (parent.Aligned.Children is not null)
                {
                    JsonArray childNodes = [];
                    foreach (var child in parent.Aligned.Children)
                    {
                        JsonObject childNode = new() { ["childCode"] = child.ChildCode };
                        if (child.ChildValue is not null)
                        {
                            childNode["childValue"] = child.ChildValue;
                        }
                        if (child.ExtensionChildren is not null)
                        {
                            JsonArray extensionChildNodes = [];
                            foreach (var extensionChild in child.ExtensionChildren)
                            {
                                JsonObject extensionChildNode = new()
                                {
                                    ["extensionChildCode"] = extensionChild.ExtensionChildCode,
                                };
                                if (extensionChild.ExtensionChildValue is not null)
                                {
                                    extensionChildNode["extensionChildValue"] =
                                        extensionChild.ExtensionChildValue;
                                }
                                extensionChildNodes.Add(extensionChildNode);
                            }
                            childNode["extensionChildren"] = extensionChildNodes;
                        }
                        childNodes.Add(childNode);
                    }
                    alignedNode["children"] = childNodes;
                }

                parentNode["_ext"] = new JsonObject { ["aligned"] = alignedNode };
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

    public static ImmutableArray<SemanticIdentityPart> AlignedChildIdentity(string childCode) =>
        [new SemanticIdentityPart("childCode", JsonValue.Create(childCode), IsPresent: true)];

    public static CollectionRowAddress AlignedChildCollectionRowAddress(
        string parentCode,
        string childCode
    ) => new(AlignedChildScope, AlignedScopeAddress(parentCode), AlignedChildIdentity(childCode));

    public static ImmutableArray<SemanticIdentityPart> ExtensionChildIdentity(string extensionChildCode) =>
        [
            new SemanticIdentityPart(
                "extensionChildCode",
                JsonValue.Create(extensionChildCode),
                IsPresent: true
            ),
        ];

    public static ScopeInstanceAddress AlignedChildContainingScopeAddress(string parentCode, string childCode)
    {
        var alignedScopeAncestors = AlignedScopeAddress(parentCode).AncestorCollectionInstances;
        return new ScopeInstanceAddress(
            AlignedChildScope,
            alignedScopeAncestors.Add(
                new AncestorCollectionInstance(AlignedChildScope, AlignedChildIdentity(childCode))
            )
        );
    }

    public static CollectionRowAddress ExtensionChildCollectionRowAddress(
        string parentCode,
        string childCode,
        string extensionChildCode
    ) =>
        new(
            ExtensionChildScope,
            AlignedChildContainingScopeAddress(parentCode, childCode),
            ExtensionChildIdentity(extensionChildCode)
        );

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestParentItem> requestParentItems,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestAlignedScope> requestAlignedScopes,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredParentRow> storedParentRows,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredAlignedScope> storedAlignedScopes,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow>? storedAlignedChildRows =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem>? requestAlignedChildItems =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow>? storedExtensionChildRows =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem>? requestExtensionChildItems =
            null,
        bool rootCreatable = true,
        string profileName = "collection-aligned-extension-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var visibleRequestItemsBuilder = ImmutableArray.CreateBuilder<VisibleRequestCollectionItem>();
        visibleRequestItemsBuilder.AddRange(
            requestParentItems.Select(item => new VisibleRequestCollectionItem(
                ParentCollectionRowAddress(item.ParentCode),
                item.Creatable,
                $"$.parents[{item.ArrayIndex}]"
            ))
        );
        if (requestAlignedChildItems is not null)
        {
            visibleRequestItemsBuilder.AddRange(
                requestAlignedChildItems.Select(item => new VisibleRequestCollectionItem(
                    AlignedChildCollectionRowAddress(item.ParentCode, item.ChildCode),
                    item.Creatable,
                    $"$.parents[{item.ParentArrayIndex}]._ext.aligned.children[{item.ChildArrayIndex}]"
                ))
            );
        }
        if (requestExtensionChildItems is not null)
        {
            visibleRequestItemsBuilder.AddRange(
                requestExtensionChildItems.Select(item => new VisibleRequestCollectionItem(
                    ExtensionChildCollectionRowAddress(
                        item.ParentCode,
                        item.ChildCode,
                        item.ExtensionChildCode
                    ),
                    item.Creatable,
                    $"$.parents[{item.ParentArrayIndex}]._ext.aligned.children[{item.ChildArrayIndex}].extensionChildren[{item.ExtensionChildArrayIndex}]"
                ))
            );
        }
        var visibleRequestItems = visibleRequestItemsBuilder.ToImmutable();

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
            StoredStateProjectionInvoker: new PostgresqlProfileCollectionAlignedExtensionProjectionInvoker(
                [.. storedParentRows],
                [.. storedAlignedScopes],
                [.. (storedAlignedChildRows ?? [])],
                [.. (storedExtensionChildRows ?? [])]
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
                    InstanceName: "PostgresqlProfileCollectionAlignedExtension",
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
            UpdateCascadeHandler: new PostgresqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpsertResult> ExecuteProfiledPostAsync(
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
                    InstanceName: "PostgresqlProfileCollectionAlignedExtension",
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
            UpdateCascadeHandler: new PostgresqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
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
                    InstanceName: "PostgresqlProfileCollectionAlignedExtension",
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
            UpdateCascadeHandler: new PostgresqlProfileCollectionAlignedExtensionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileCollectionAlignedExtensionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<long> ReadDocumentIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        rows.Should().HaveCount(1);
        return GetInt64(rows[0], "DocumentId");
    }

    public static Task<long> ReadDocumentCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."Document";
            """
        );

    public static async Task<
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionParentRow>
    > ReadParentRowsAsync(PostgresqlGeneratedDdlTestDatabase database, DocumentUuid documentUuid)
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

        return rows.Select(row => new PostgresqlProfileCollectionAlignedExtensionParentRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "ParentCode"),
                GetString(row, "ParentName")
            ))
            .ToArray();
    }

    public static async Task<PostgresqlProfileCollectionAlignedExtensionAlignedRow?> TryReadAlignedRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ext."BaseCollectionItemId",
                ext."ParentResource_DocumentId",
                ext."AlignedVisibleScalar",
                ext."AlignedHiddenScalar"
            FROM "aligned"."ParentResourceExtensionParent" ext
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ext."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Count == 0
            ? null
            : new PostgresqlProfileCollectionAlignedExtensionAlignedRow(
                GetInt64(rows[0], "BaseCollectionItemId"),
                GetInt64(rows[0], "ParentResource_DocumentId"),
                GetNullableString(rows[0], "AlignedVisibleScalar"),
                GetNullableString(rows[0], "AlignedHiddenScalar")
            );
    }

    public static async Task<int> CountAlignedRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var scalar = await database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "aligned"."ParentResourceExtensionParent" ext
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ext."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
        return checked((int)scalar);
    }

    public static Task<long> ReadAlignedChildRowCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "aligned"."ParentResourceExtensionParentChildren";
            """
        );

    public static async Task<
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow>
    > ReadAlignedChildRowsAsync(PostgresqlGeneratedDdlTestDatabase database, DocumentUuid documentUuid)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                c."CollectionItemId",
                c."BaseCollectionItemId",
                c."ParentResource_DocumentId",
                c."Ordinal",
                c."ChildCode",
                c."ChildValue"
            FROM "aligned"."ParentResourceExtensionParentChildren" c
            INNER JOIN "dms"."Document" d ON d."DocumentId" = c."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid
            ORDER BY c."BaseCollectionItemId", c."Ordinal", c."CollectionItemId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new PostgresqlProfileCollectionAlignedExtensionAlignedChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "ChildCode"),
                GetNullableString(row, "ChildValue")
            ))
            .ToArray();
    }

    public static async Task<
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow>
    > ReadExtensionChildRowsAsync(PostgresqlGeneratedDdlTestDatabase database, DocumentUuid documentUuid)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ec."CollectionItemId",
                ec."ParentCollectionItemId",
                ec."ParentResource_DocumentId",
                ec."Ordinal",
                ec."ExtensionChildCode",
                ec."ExtensionChildValue"
            FROM "aligned"."ParentResourceExtensionParentChildrenExtensionChildren" ec
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ec."ParentResource_DocumentId"
            WHERE d."DocumentUuid" = @documentUuid
            ORDER BY ec."ParentCollectionItemId", ec."Ordinal", ec."CollectionItemId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        return rows.Select(row => new PostgresqlProfileCollectionAlignedExtensionExtensionChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "ExtensionChildCode"),
                GetNullableString(row, "ExtensionChildValue")
            ))
            .ToArray();
    }

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

internal abstract class PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    protected const int ParentResourceId = 9301;
    protected const string ParentCode = "PARENT-A";
    protected static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("ee000001-0000-0000-0000-000000000001")
    );

    protected PostgresqlGeneratedDdlFixture Fixture = null!;
    protected MappingSet MappingSet = null!;
    protected PostgresqlGeneratedDdlTestDatabase Database = null!;
    protected ServiceProvider ServiceProvider = null!;

    [OneTimeSetUp]
    public async Task BaseOneTimeSetUp()
    {
        Fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileCollectionAlignedExtensionSupport.FixtureRelativePath
        );
        MappingSet = Fixture.MappingSet;
        Database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(Fixture.GeneratedDdl);
        ServiceProvider = PostgresqlProfileCollectionAlignedExtensionSupport.CreateServiceProvider();
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
        MappingSet.WritePlansByResource[PostgresqlProfileCollectionAlignedExtensionSupport.ParentResource];

    protected BackendProfileWriteContext CreateProfileContext(
        JsonNode requestBody,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestParentItem> requestParentItems,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestAlignedScope> requestAlignedScopes,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredParentRow>? storedParentRows = null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredAlignedScope>? storedAlignedScopes =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow>? storedAlignedChildRows =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem>? requestAlignedChildItems =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow>? storedExtensionChildRows =
            null,
        IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem>? requestExtensionChildItems =
            null
    ) =>
        PostgresqlProfileCollectionAlignedExtensionSupport.CreateProfileContext(
            WritePlan,
            requestBody.DeepClone(),
            requestParentItems,
            requestAlignedScopes,
            storedParentRows ?? [],
            storedAlignedScopes ?? [],
            storedAlignedChildRows,
            requestAlignedChildItems,
            storedExtensionChildRows,
            requestExtensionChildItems
        );

    protected Task<UpsertResult> ExecuteProfiledPostAsync(
        JsonNode writeBody,
        BackendProfileWriteContext profileContext,
        string traceLabel
    ) =>
        PostgresqlProfileCollectionAlignedExtensionSupport.ExecuteProfiledPostAsync(
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
        PostgresqlProfileCollectionAlignedExtensionSupport.ExecuteProfiledPutAsync(
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
        var seedResult = await PostgresqlProfileCollectionAlignedExtensionSupport.SeedAsync(
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

    protected static PostgresqlProfileCollectionAlignedExtensionRequestParentItem RequestParent() =>
        new(ParentCode, ArrayIndex: 0);

    protected static PostgresqlProfileCollectionAlignedExtensionStoredParentRow StoredParent() =>
        new(ParentCode, []);

    protected static PostgresqlProfileCollectionAlignedExtensionRequestAlignedScope RequestAligned(
        ProfileVisibilityKind visibility,
        bool creatable
    ) => new(ParentCode, visibility, creatable);

    protected static PostgresqlProfileCollectionAlignedExtensionStoredAlignedScope StoredAligned(
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_create_request_for_a_resource_with_a_visible_present_aligned_extension_scope
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionParentRow> _parentRows = null!;
    private PostgresqlProfileCollectionAlignedExtensionAlignedRow? _alignedRow;
    private long _alignedChildRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Created Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput("CreatedVisible", "CreatedHidden")
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
            "pgsql-profile-collection-aligned-visible-present-post"
        );
        _parentRows = await PostgresqlProfileCollectionAlignedExtensionSupport.ReadParentRowsAsync(
            Database,
            DocumentUuid
        );
        _alignedRow = await PostgresqlProfileCollectionAlignedExtensionSupport.TryReadAlignedRowAsync(
            Database,
            DocumentUuid
        );
        _alignedChildRowCount =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowCountAsync(Database);
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
            .Match<PostgresqlProfileCollectionAlignedExtensionParentRow>(row =>
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_create_request_for_a_resource_with_a_visible_absent_aligned_extension_scope
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Created Parent")
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisibleAbsent, creatable: true)]
        );

        _postResult = await ExecuteProfiledPostAsync(
            writeBody,
            profileContext,
            "pgsql-profile-collection-aligned-visible-absent-post"
        );
        _alignedRowCount = await PostgresqlProfileCollectionAlignedExtensionSupport.CountAlignedRowsAsync(
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_create_request_for_a_resource_with_a_hidden_aligned_extension_scope_and_request_data
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private long _documentCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Hidden Request Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput("HiddenVisible", "HiddenHidden")
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
            "pgsql-profile-collection-aligned-hidden-request-data-post"
        );
        _documentCount = await PostgresqlProfileCollectionAlignedExtensionSupport.ReadDocumentCountAsync(
            Database
        );
    }

    [Test]
    public void It_returns_profile_data_policy_failure() =>
        _postResult.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>(FormatResult(_postResult));

    [Test]
    public void It_rolls_back_the_write() => _documentCount.Should().Be(0);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_visible_present_to_visible_present
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private PostgresqlProfileCollectionAlignedExtensionAlignedRow? _alignedRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput("SeedVisible", "SeedHidden")
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-visible-present-seed");

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput("UpdatedVisible", "UpdatedHidden")
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
            "pgsql-profile-collection-aligned-visible-present-put"
        );
        _alignedRow = await PostgresqlProfileCollectionAlignedExtensionSupport.TryReadAlignedRowAsync(
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_visible_present_to_visible_absent
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput("SeedVisible", "SeedHidden")
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-visible-absent-seed");

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Updated Parent")
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
            "pgsql-profile-collection-aligned-visible-absent-put"
        );
        _alignedRowCount = await PostgresqlProfileCollectionAlignedExtensionSupport.CountAlignedRowsAsync(
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_hidden_in_storage
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private PostgresqlProfileCollectionAlignedExtensionAlignedRow? _alignedRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput("StoredVisible", "StoredHidden")
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-hidden-storage-seed");

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Updated Parent")
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
            "pgsql-profile-collection-aligned-hidden-storage-put"
        );
        _alignedRow = await PostgresqlProfileCollectionAlignedExtensionSupport.TryReadAlignedRowAsync(
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
[Category("PostgresqlIntegration")]
internal class Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionParentRow> _parentRowsAfterPut = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(ParentCode, "Seed Parent")
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-create-denied-seed");

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Rejected Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "RejectedVisible",
                    "RejectedHidden"
                )
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
            "pgsql-profile-collection-aligned-create-denied-put"
        );
        _parentRowsAfterPut = await PostgresqlProfileCollectionAlignedExtensionSupport.ReadParentRowsAsync(
            Database,
            DocumentUuid
        );
        _alignedRowCount = await PostgresqlProfileCollectionAlignedExtensionSupport.CountAlignedRowsAsync(
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

// ── Aligned-extension child collection scenarios ─────────────────────────────────────────
// PostgreSQL mirror of MssqlProfileCollectionAlignedExtensionMergeTests's aligned-extension-
// child fixtures. Exercises the runtime merge path against
// "aligned"."ParentResourceExtensionParentChildren" so the Slice 5 acceptance gap noted by
// Agent 1 is closed on PostgreSQL as well as MSSQL.

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_create_request_with_aligned_extension_child_collection_items
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Created Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "CreatedVisible",
                    "CreatedHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildA", "ValueA"),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildB", "ValueB"),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            requestAlignedChildItems:
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildB",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 1
                ),
            ]
        );

        _postResult = await ExecuteProfiledPostAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-aligned-extension-child-create-post"
        );
        _childRows = await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowsAsync(
            Database,
            DocumentUuid
        );
    }

    [Test]
    public void It_returns_insert_success() =>
        _postResult.Should().BeOfType<UpsertResult.InsertSuccess>(FormatResult(_postResult));

    [Test]
    public void It_inserts_two_aligned_extension_child_rows() => _childRows.Should().HaveCount(2);

    [Test]
    public void It_inserts_aligned_extension_child_rows_in_request_order()
    {
        _childRows.Select(r => r.ChildCode).Should().Equal("ChildA", "ChildB");
        _childRows.Select(r => r.Ordinal).Should().Equal(1, 2);
        _childRows.Select(r => r.ChildValue).Should().Equal("ValueA", "ValueB");
    }

    [Test]
    public void It_associates_aligned_extension_child_rows_with_the_parent_collection_item()
    {
        var distinctBaseIds = _childRows.Select(r => r.BaseCollectionItemId).Distinct().ToArray();
        distinctBaseIds.Should().ContainSingle();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow> _childRowsBeforePut =
        null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow> _childRowsAfterPut =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "OriginalA"
                        ),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildB",
                            "OriginalB"
                        ),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-aligned-extension-child-update-seed");
        _childRowsBeforePut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowsAsync(
                Database,
                DocumentUuid
            );

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "OriginalA"
                        ),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildB",
                            "ChangedB"
                        ),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)],
            [
                new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
                    ParentCode,
                    "ChildA",
                    []
                ),
                new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
                    ParentCode,
                    "ChildB",
                    []
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildB",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 1
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-aligned-extension-child-update-put"
        );
        _childRowsAfterPut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_preserves_the_aligned_extension_child_row_count() =>
        _childRowsAfterPut.Should().HaveCount(2);

    [Test]
    public void It_updates_only_the_modified_aligned_extension_child_value()
    {
        _childRowsAfterPut.Single(r => r.ChildCode == "ChildA").ChildValue.Should().Be("OriginalA");
        _childRowsAfterPut.Single(r => r.ChildCode == "ChildB").ChildValue.Should().Be("ChangedB");
    }

    [Test]
    public void It_preserves_the_aligned_extension_child_ordinals()
    {
        _childRowsAfterPut.Single(r => r.ChildCode == "ChildA").Ordinal.Should().Be(1);
        _childRowsAfterPut.Single(r => r.ChildCode == "ChildB").Ordinal.Should().Be(2);
    }

    [Test]
    public void It_updates_matched_aligned_extension_child_rows_in_place_preserving_collection_item_ids()
    {
        var seededIdByCode = _childRowsBeforePut.ToDictionary(r => r.ChildCode, r => r.CollectionItemId);
        seededIdByCode.Should().ContainKeys("ChildA", "ChildB");

        _childRowsAfterPut
            .Single(r => r.ChildCode == "ChildA")
            .CollectionItemId.Should()
            .Be(seededIdByCode["ChildA"]);
        _childRowsAfterPut
            .Single(r => r.ChildCode == "ChildB")
            .CollectionItemId.Should()
            .Be(seededIdByCode["ChildB"]);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_omitting_an_aligned_extension_child
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow> _childRowsAfterPut =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildA", "ValueA"),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildB", "ValueB"),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-aligned-extension-child-delete-seed");

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildA", "ValueA"),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)],
            [
                new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
                    ParentCode,
                    "ChildA",
                    []
                ),
                new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
                    ParentCode,
                    "ChildB",
                    []
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-aligned-extension-child-delete-put"
        );
        _childRowsAfterPut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_deletes_the_omitted_aligned_extension_child_row()
    {
        _childRowsAfterPut.Should().ContainSingle();
        _childRowsAfterPut[0].ChildCode.Should().Be("ChildA");
    }

    [Test]
    public void It_recomputes_the_surviving_aligned_extension_child_ordinal_to_one() =>
        _childRowsAfterPut[0].Ordinal.Should().Be(1);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow> _childRowsBeforePut =
        null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionAlignedChildRow> _childRowsAfterPut =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildA", "ValueA"),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildB", "ValueB"),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-aligned-extension-child-reorder-seed");
        _childRowsBeforePut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowsAsync(
                Database,
                DocumentUuid
            );

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildB", "ValueB"),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildA", "ValueA"),
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput("ChildC", "ValueC"),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)],
            [
                new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
                    ParentCode,
                    "ChildA",
                    []
                ),
                new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(
                    ParentCode,
                    "ChildB",
                    []
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildB",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 1
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildC",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 2
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-aligned-extension-child-reorder-put"
        );
        _childRowsAfterPut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadAlignedChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_yields_three_aligned_extension_child_rows_after_reorder_and_insert() =>
        _childRowsAfterPut.Should().HaveCount(3);

    [Test]
    public void It_assigns_aligned_extension_child_ordinals_in_new_request_order()
    {
        _childRowsAfterPut.Select(r => r.ChildCode).Should().Equal("ChildB", "ChildA", "ChildC");
        _childRowsAfterPut.Select(r => r.Ordinal).Should().Equal(1, 2, 3);
    }

    [Test]
    public void It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child()
    {
        var seededIdByCode = _childRowsBeforePut.ToDictionary(r => r.ChildCode, r => r.CollectionItemId);
        seededIdByCode.Should().ContainKeys("ChildA", "ChildB");

        var afterIdByCode = _childRowsAfterPut.ToDictionary(r => r.ChildCode, r => r.CollectionItemId);

        afterIdByCode["ChildA"].Should().Be(seededIdByCode["ChildA"]);
        afterIdByCode["ChildB"].Should().Be(seededIdByCode["ChildB"]);
        afterIdByCode["ChildC"].Should().NotBe(seededIdByCode["ChildA"]).And.NotBe(seededIdByCode["ChildB"]);
    }
}

// ── Nested-extension child collection scenarios ──────────────────────────────────────────
// PostgreSQL mirror of MssqlProfileCollectionAlignedExtensionMergeTests's nested-extension-
// child fixtures. Exercises the runtime merge path against
// "aligned"."ParentResourceExtensionParentChildrenExtensionChildren" (one level deeper than
// the aligned-extension child collection at
// $.parents[*]._ext.aligned.children[*].extensionChildren[*]) so the slice's deeper-recursion
// acceptance gap is closed on PostgreSQL as well as MSSQL.

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_create_request_with_nested_extension_children
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow> _extensionChildRows =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Created Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "CreatedVisible",
                    "CreatedHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "AlphaValue"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildBeta",
                                    "BetaValue"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            requestAlignedChildItems:
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
            ],
            requestExtensionChildItems:
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 1
                ),
            ]
        );

        _postResult = await ExecuteProfiledPostAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-nested-extension-child-create-post"
        );
        _extensionChildRows =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadExtensionChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_insert_success() =>
        _postResult.Should().BeOfType<UpsertResult.InsertSuccess>(FormatResult(_postResult));

    [Test]
    public void It_inserts_two_nested_extension_child_rows() => _extensionChildRows.Should().HaveCount(2);

    [Test]
    public void It_inserts_nested_extension_child_rows_in_request_order()
    {
        _extensionChildRows.Select(r => r.ExtensionChildCode).Should().Equal("ExtChildAlpha", "ExtChildBeta");
        _extensionChildRows.Select(r => r.Ordinal).Should().Equal(1, 2);
        _extensionChildRows.Select(r => r.ExtensionChildValue).Should().Equal("AlphaValue", "BetaValue");
    }

    [Test]
    public void It_associates_nested_extension_child_rows_with_the_aligned_child_collection_item()
    {
        var distinctParentIds = _extensionChildRows
            .Select(r => r.ParentCollectionItemId)
            .Distinct()
            .ToArray();
        distinctParentIds.Should().ContainSingle();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_modifying_a_nested_extension_child_value
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow> _extensionChildRowsBeforePut =
        null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow> _extensionChildRowsAfterPut =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "OriginalAlpha"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildBeta",
                                    "OriginalBeta"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-nested-extension-child-update-seed");
        _extensionChildRowsBeforePut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadExtensionChildRowsAsync(
                Database,
                DocumentUuid
            );

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "OriginalAlpha"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildBeta",
                                    "ChangedBeta"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)],
            [new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(ParentCode, "ChildA", [])],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    []
                ),
                new PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    []
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 1
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-nested-extension-child-update-put"
        );
        _extensionChildRowsAfterPut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadExtensionChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_preserves_the_nested_extension_child_row_count() =>
        _extensionChildRowsAfterPut.Should().HaveCount(2);

    [Test]
    public void It_updates_only_the_modified_nested_extension_child_value()
    {
        _extensionChildRowsAfterPut
            .Single(r => r.ExtensionChildCode == "ExtChildAlpha")
            .ExtensionChildValue.Should()
            .Be("OriginalAlpha");
        _extensionChildRowsAfterPut
            .Single(r => r.ExtensionChildCode == "ExtChildBeta")
            .ExtensionChildValue.Should()
            .Be("ChangedBeta");
    }

    [Test]
    public void It_updates_matched_nested_extension_child_rows_in_place_preserving_collection_item_ids()
    {
        var seededIdByCode = _extensionChildRowsBeforePut.ToDictionary(
            r => r.ExtensionChildCode,
            r => r.CollectionItemId
        );
        seededIdByCode.Should().ContainKeys("ExtChildAlpha", "ExtChildBeta");

        _extensionChildRowsAfterPut
            .Single(r => r.ExtensionChildCode == "ExtChildAlpha")
            .CollectionItemId.Should()
            .Be(seededIdByCode["ExtChildAlpha"]);
        _extensionChildRowsAfterPut
            .Single(r => r.ExtensionChildCode == "ExtChildBeta")
            .CollectionItemId.Should()
            .Be(seededIdByCode["ExtChildBeta"]);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_omitting_a_nested_extension_child
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow> _extensionChildRowsAfterPut =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "AlphaValue"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildBeta",
                                    "BetaValue"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-nested-extension-child-delete-seed");

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "AlphaValue"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)],
            [new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(ParentCode, "ChildA", [])],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    []
                ),
                new PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    []
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-nested-extension-child-delete-put"
        );
        _extensionChildRowsAfterPut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadExtensionChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_deletes_the_omitted_nested_extension_child_row()
    {
        _extensionChildRowsAfterPut.Should().ContainSingle();
        _extensionChildRowsAfterPut[0].ExtensionChildCode.Should().Be("ExtChildAlpha");
    }

    [Test]
    public void It_recomputes_the_surviving_nested_extension_child_ordinal_to_one() =>
        _extensionChildRowsAfterPut[0].Ordinal.Should().Be(1);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpdateResult _putResult = null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow> _extensionChildRowsBeforePut =
        null!;
    private IReadOnlyList<PostgresqlProfileCollectionAlignedExtensionExtensionChildRow> _extensionChildRowsAfterPut =
        null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Seed Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "AlphaValue"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildBeta",
                                    "BetaValue"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-nested-extension-child-reorder-seed");
        _extensionChildRowsBeforePut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadExtensionChildRowsAsync(
                Database,
                DocumentUuid
            );

        var writeBody = PostgresqlProfileCollectionAlignedExtensionSupport.CreateParentResourceBody(
            ParentResourceId,
            new PostgresqlProfileCollectionAlignedExtensionParentInput(
                ParentCode,
                "Updated Parent",
                new PostgresqlProfileCollectionAlignedExtensionAlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new PostgresqlProfileCollectionAlignedExtensionAlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildBeta",
                                    "BetaValue"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildAlpha",
                                    "AlphaValue"
                                ),
                                new PostgresqlProfileCollectionAlignedExtensionExtensionChildInput(
                                    "ExtChildGamma",
                                    "GammaValue"
                                ),
                            ]
                        ),
                    ]
                )
            )
        );
        var profileContext = CreateProfileContext(
            writeBody,
            [RequestParent()],
            [RequestAligned(ProfileVisibilityKind.VisiblePresent, creatable: true)],
            [StoredParent()],
            [StoredAligned(ProfileVisibilityKind.VisiblePresent)],
            [new PostgresqlProfileCollectionAlignedExtensionStoredAlignedChildRow(ParentCode, "ChildA", [])],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestAlignedChildItem(
                    ParentCode,
                    "ChildA",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    []
                ),
                new PostgresqlProfileCollectionAlignedExtensionStoredExtensionChildRow(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    []
                ),
            ],
            [
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 1
                ),
                new PostgresqlProfileCollectionAlignedExtensionRequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildGamma",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 2
                ),
            ]
        );

        _putResult = await ExecuteProfiledPutAsync(
            writeBody,
            profileContext,
            "postgres-profile-collection-nested-extension-child-reorder-put"
        );
        _extensionChildRowsAfterPut =
            await PostgresqlProfileCollectionAlignedExtensionSupport.ReadExtensionChildRowsAsync(
                Database,
                DocumentUuid
            );
    }

    [Test]
    public void It_returns_update_success() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>(FormatResult(_putResult));

    [Test]
    public void It_yields_three_nested_extension_child_rows_after_reorder_and_insert() =>
        _extensionChildRowsAfterPut.Should().HaveCount(3);

    [Test]
    public void It_assigns_nested_extension_child_ordinals_in_new_request_order()
    {
        _extensionChildRowsAfterPut
            .Select(r => r.ExtensionChildCode)
            .Should()
            .Equal("ExtChildBeta", "ExtChildAlpha", "ExtChildGamma");
        _extensionChildRowsAfterPut.Select(r => r.Ordinal).Should().Equal(1, 2, 3);
    }

    [Test]
    public void It_preserves_collection_item_ids_for_matched_nested_extension_children_and_assigns_a_new_id_to_the_inserted_child()
    {
        var seededIdByCode = _extensionChildRowsBeforePut.ToDictionary(
            r => r.ExtensionChildCode,
            r => r.CollectionItemId
        );
        seededIdByCode.Should().ContainKeys("ExtChildAlpha", "ExtChildBeta");

        var afterIdByCode = _extensionChildRowsAfterPut.ToDictionary(
            r => r.ExtensionChildCode,
            r => r.CollectionItemId
        );

        afterIdByCode["ExtChildAlpha"].Should().Be(seededIdByCode["ExtChildAlpha"]);
        afterIdByCode["ExtChildBeta"].Should().Be(seededIdByCode["ExtChildBeta"]);
        afterIdByCode["ExtChildGamma"]
            .Should()
            .NotBe(seededIdByCode["ExtChildAlpha"])
            .And.NotBe(seededIdByCode["ExtChildBeta"]);
    }
}
