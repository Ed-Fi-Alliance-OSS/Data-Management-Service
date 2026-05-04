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
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.ProfileNestedCollectionScenarios;

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

internal sealed class PostgresqlProfileNestedProjectionInvoker(
    ImmutableArray<StoredParentRow> storedParentRows,
    ImmutableArray<StoredChildRow> storedChildRows,
    ImmutableArray<StoredRootExtChildRow> storedRootExtChildRows,
    StoredRootExtScope? storedRootExtScope
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
                    new ScopeInstanceAddress(RootExtScope, []),
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
                    ParentRowAddress(parentRow.ParentCode),
                    parentRow.HiddenMemberPaths
                )
            );
        }

        foreach (var childRow in storedChildRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    ChildRowAddress(childRow.ParentCode, childRow.ChildCode),
                    childRow.HiddenMemberPaths
                )
            );
        }

        foreach (var rootExtChildRow in storedRootExtChildRows)
        {
            visibleStoredRows.Add(
                new VisibleStoredCollectionRow(
                    RootExtChildRowAddress(rootExtChildRow.RootExtChildCode),
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

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<RequestParentItem> requestParentItems,
        IReadOnlyList<RequestChildItem>? requestChildItems = null,
        IReadOnlyList<RequestRootExtChildItem>? requestRootExtChildItems = null,
        RequestRootExtScope? requestRootExtScope = null,
        IReadOnlyList<StoredParentRow>? storedParentRows = null,
        IReadOnlyList<StoredChildRow>? storedChildRows = null,
        IReadOnlyList<StoredRootExtChildRow>? storedRootExtChildRows = null,
        StoredRootExtScope? storedRootExtScope = null,
        bool rootCreatable = true,
        string profileName = "nested-and-root-ext-profile"
    ) =>
        ProfileNestedCollectionScenarios.CreateProfileContext(
            writePlan,
            requestBody,
            requestParentItems,
            new PostgresqlProfileNestedProjectionInvoker(
                [.. storedParentRows ?? []],
                [.. storedChildRows ?? []],
                [.. storedRootExtChildRows ?? []],
                storedRootExtScope
            ),
            requestChildItems,
            requestRootExtChildItems,
            requestRootExtScope,
            rootCreatable,
            profileName
        );

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

    public static async Task<IReadOnlyList<ParentRow>> ReadParentRowsAsync(
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

        return rows.Select(row => new ParentRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "ParentCode"),
                GetNullableString(row, "ParentName")
            ))
            .ToArray();
    }

    public static async Task<IReadOnlyList<ChildRow>> ReadChildRowsAsync(
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

        return rows.Select(row => new ChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "ChildCode"),
                GetNullableString(row, "ChildValue")
            ))
            .ToArray();
    }

    public static async Task<RootExtRow?> TryReadRootExtRowAsync(
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
            : new RootExtRow(
                GetInt64(rows[0], "DocumentId"),
                GetNullableString(rows[0], "RootExtVisibleScalar"),
                GetNullableString(rows[0], "RootExtHiddenScalar")
            );
    }

    public static async Task<IReadOnlyList<RootExtChildRow>> ReadRootExtChildRowsAsync(
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

        return rows.Select(row => new RootExtChildRow(
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
        Fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
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

    protected ResourceWritePlan WritePlan => MappingSet.WritePlansByResource[ParentResource];

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
    private IReadOnlyList<ChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(
                    ParentCode,
                    "Seed Parent",
                    Children:
                    [
                        new ChildInput(VisibleChildCodeA, "SeedV1"),
                        new ChildInput(VisibleChildCodeB, "SeedV2"),
                        new ChildInput(HiddenChildCode, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-vru-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(
                    ParentCode,
                    "Updated Parent",
                    Children:
                    [
                        new ChildInput(VisibleChildCodeA, "UpdatedV1"),
                        new ChildInput(VisibleChildCodeB, "UpdatedV2"),
                    ]
                ),
            ]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new RequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems:
            [
                new RequestChildItem(ParentCode, VisibleChildCodeA, 0, 0),
                new RequestChildItem(ParentCode, VisibleChildCodeB, 0, 1),
            ],
            storedParentRows: [new StoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new StoredChildRow(ParentCode, VisibleChildCodeA, []),
                new StoredChildRow(ParentCode, VisibleChildCodeB, []),
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
    private IReadOnlyList<ChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(
                    ParentCode,
                    "Seed Parent",
                    Children:
                    [
                        new ChildInput(VisibleChildCodeA, "SeedV1"),
                        new ChildInput(VisibleChildCodeB, "SeedV2"),
                        new ChildInput(HiddenChildCode, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-vrd-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            parents: [new ParentInput(ParentCode, "Updated Parent", Children: [])]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new RequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [],
            storedParentRows: [new StoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new StoredChildRow(ParentCode, VisibleChildCodeA, []),
                new StoredChildRow(ParentCode, VisibleChildCodeB, []),
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
    private IReadOnlyList<RootExtChildRow> _rootExtChildRows = null!;
    private RootExtRow? _rootExtRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            rootExt: new RootExtInput(
                "SeedVisible",
                "SeedHidden",
                Children:
                [
                    new RootExtChildInput(RootExtChildCodeA, "SeedRECV1"),
                    new RootExtChildInput(RootExtChildCodeB, "SeedRECV2"),
                ]
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-rec-update-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            rootExt: new RootExtInput(
                "UpdatedVisible",
                "UpdatedHidden",
                Children:
                [
                    new RootExtChildInput(RootExtChildCodeA, "UpdatedRECV1"),
                    new RootExtChildInput(RootExtChildCodeB, "UpdatedRECV2"),
                ]
            )
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [],
            requestRootExtChildItems:
            [
                new RequestRootExtChildItem(RootExtChildCodeA, ArrayIndex: 0),
                new RequestRootExtChildItem(RootExtChildCodeB, ArrayIndex: 1),
            ],
            requestRootExtScope: new RequestRootExtScope(
                ProfileVisibilityKind.VisiblePresent,
                Creatable: true
            ),
            storedRootExtChildRows:
            [
                new StoredRootExtChildRow(RootExtChildCodeA, []),
                new StoredRootExtChildRow(RootExtChildCodeB, []),
            ],
            storedRootExtScope: new StoredRootExtScope(ProfileVisibilityKind.VisiblePresent, [])
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
    private IReadOnlyList<RootExtChildRow> _rootExtChildRows = null!;
    private RootExtRow? _rootExtRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            rootExt: new RootExtInput(
                "SeedVisible",
                "SeedHidden",
                Children:
                [
                    new RootExtChildInput(RootExtChildCodeA, "SeedRECH1"),
                    new RootExtChildInput(RootExtChildCodeB, "SeedRECH2"),
                ]
            )
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-hidden-rec-seed");

        // Request body keeps only the root document. Hidden extension scope means the
        // synthesizer never sees a payload for the extension and must not touch storage.
        var writeBody = CreateParentResourceBody(ParentResourceId);

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [],
            requestRootExtScope: new RequestRootExtScope(ProfileVisibilityKind.Hidden, Creatable: false),
            storedRootExtChildRows:
            [
                new StoredRootExtChildRow(RootExtChildCodeA, []),
                new StoredRootExtChildRow(RootExtChildCodeB, []),
            ],
            storedRootExtScope: new StoredRootExtScope(ProfileVisibilityKind.Hidden, [])
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
/// Scenario 5: Provider-level two-level update-allowed/create-denied companion to the
/// synthesizer-level three-level chain coverage. Slice 5 intentionally keeps this
/// provider fixture at parent -> child only; the literal provider-level
/// parent -> child -> grandchild fixture is deferred to Slice 7 parity/hardening
/// (see 07-parity-and-hardening.md and 05-nested-and-extension-collection-merge.md
/// boundary clarification). The profile keeps parents and their children visible, but
/// with creatable=false on the children scope. The seeded parent has no stored children;
/// the request adds a new child under that parent, which the planner must reject with a
/// creatability rejection (synthesizer returns rejection -> profile data policy failure).
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
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            parents: [new ParentInput(ParentACode, "Seed", Children: [])]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-3l-seed");

        // Request adds a new child under parent A; profile makes the child scope visible
        // but NOT creatable, so the planner must reject the insert.
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(ParentACode, "Updated", Children: [new ChildInput(ChildCodeA, "NewValue")]),
            ]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new RequestParentItem(ParentACode, ArrayIndex: 0)],
            requestChildItems:
            [
                new RequestChildItem(
                    ParentACode,
                    ChildCodeA,
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    Creatable: false
                ),
            ],
            storedParentRows: [new StoredParentRow(ParentACode, [])]
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
    private IReadOnlyList<ChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(
                    ParentCode,
                    "Seed",
                    Children:
                    [
                        new ChildInput(VisibleChildA, "SeedV1"),
                        new ChildInput(VisibleChildB, "SeedV2"),
                        new ChildInput(HiddenChild, "SeedHidden"),
                    ]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-dah-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            parents: [new ParentInput(ParentCode, "Updated", Children: [])]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new RequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [],
            storedParentRows: [new StoredParentRow(ParentCode, [])],
            storedChildRows:
            [
                new StoredChildRow(ParentCode, VisibleChildA, []),
                new StoredChildRow(ParentCode, VisibleChildB, []),
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
    private IReadOnlyList<ChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        // Stored child carries the hidden value; the matched-row overlay must preserve
        // it on the merge regardless of what the request sends.
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(
                    ParentCode,
                    "Seed",
                    Children: [new ChildInput(ChildCode, "StoredHiddenValue")]
                ),
            ]
        );
        await SeedAsync(seedBody, "pgsql-profile-nested-hmp-seed");

        // Request tries to overwrite the hidden scalar; the matched-row overlay must
        // ignore the request value at that path and copy the stored value forward.
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            parents:
            [
                new ParentInput(
                    ParentCode,
                    "Updated",
                    Children: [new ChildInput(ChildCode, "RequestAttemptedValue")]
                ),
            ]
        );

        var profileContext = PostgresqlProfileNestedSupport.CreateProfileContext(
            WritePlan,
            writeBody.DeepClone(),
            requestParentItems: [new RequestParentItem(ParentCode, ArrayIndex: 0)],
            requestChildItems: [new RequestChildItem(ParentCode, ChildCode, 0, 0)],
            storedParentRows: [new StoredParentRow(ParentCode, [])],
            storedChildRows: [new StoredChildRow(ParentCode, ChildCode, HiddenMemberPaths: ["childValue"])]
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
