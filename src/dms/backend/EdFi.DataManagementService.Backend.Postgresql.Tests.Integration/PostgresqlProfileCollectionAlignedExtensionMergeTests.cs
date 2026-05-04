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
using static EdFi.DataManagementService.Backend.Tests.Common.ProfileCollectionAlignedExtensionScenarios;

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
    ImmutableArray<StoredParentRow> storedParentRows,
    ImmutableArray<StoredAlignedScope> storedAlignedScopes,
    ImmutableArray<StoredAlignedChildRow> storedAlignedChildRows,
    ImmutableArray<StoredExtensionChildRow> storedExtensionChildRows
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
                AlignedScopeAddress(scope.ParentCode),
                scope.Visibility,
                scope.HiddenMemberPaths
            ))
        );

        var visibleStoredRows = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>();
        visibleStoredRows.AddRange(
            storedParentRows.Select(row => new VisibleStoredCollectionRow(
                ParentCollectionRowAddress(row.ParentCode),
                row.HiddenMemberPaths
            ))
        );
        visibleStoredRows.AddRange(
            storedAlignedChildRows.Select(row => new VisibleStoredCollectionRow(
                AlignedChildCollectionRowAddress(row.ParentCode, row.ChildCode),
                row.HiddenMemberPaths
            ))
        );
        visibleStoredRows.AddRange(
            storedExtensionChildRows.Select(row => new VisibleStoredCollectionRow(
                ExtensionChildCollectionRowAddress(row.ParentCode, row.ChildCode, row.ExtensionChildCode),
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

internal static class PostgresqlProfileCollectionAlignedExtensionSupport
{
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

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<RequestParentItem> requestParentItems,
        IReadOnlyList<RequestAlignedScope> requestAlignedScopes,
        IReadOnlyList<StoredParentRow> storedParentRows,
        IReadOnlyList<StoredAlignedScope> storedAlignedScopes,
        IReadOnlyList<StoredAlignedChildRow>? storedAlignedChildRows = null,
        IReadOnlyList<RequestAlignedChildItem>? requestAlignedChildItems = null,
        IReadOnlyList<StoredExtensionChildRow>? storedExtensionChildRows = null,
        IReadOnlyList<RequestExtensionChildItem>? requestExtensionChildItems = null,
        bool rootCreatable = true,
        string profileName = "collection-aligned-extension-profile"
    ) =>
        ProfileCollectionAlignedExtensionScenarios.CreateProfileContext(
            writePlan,
            requestBody,
            requestParentItems,
            requestAlignedScopes,
            new PostgresqlProfileCollectionAlignedExtensionProjectionInvoker(
                [.. storedParentRows],
                [.. storedAlignedScopes],
                [.. (storedAlignedChildRows ?? [])],
                [.. (storedExtensionChildRows ?? [])]
            ),
            requestAlignedChildItems,
            requestExtensionChildItems,
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
                GetString(row, "ParentCode"),
                GetString(row, "ParentName")
            ))
            .ToArray();
    }

    public static async Task<AlignedRow?> TryReadAlignedRowAsync(
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
            : new AlignedRow(
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

    public static async Task<IReadOnlyList<AlignedChildRow>> ReadAlignedChildRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
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

        return rows.Select(row => new AlignedChildRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "ParentResource_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "ChildCode"),
                GetNullableString(row, "ChildValue")
            ))
            .ToArray();
    }

    public static async Task<IReadOnlyList<ExtensionChildRow>> ReadExtensionChildRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
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

        return rows.Select(row => new ExtensionChildRow(
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
        Fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
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

    protected ResourceWritePlan WritePlan => MappingSet.WritePlansByResource[ParentResource];

    protected BackendProfileWriteContext CreateProfileContext(
        JsonNode requestBody,
        IReadOnlyList<RequestParentItem> requestParentItems,
        IReadOnlyList<RequestAlignedScope> requestAlignedScopes,
        IReadOnlyList<StoredParentRow>? storedParentRows = null,
        IReadOnlyList<StoredAlignedScope>? storedAlignedScopes = null,
        IReadOnlyList<StoredAlignedChildRow>? storedAlignedChildRows = null,
        IReadOnlyList<RequestAlignedChildItem>? requestAlignedChildItems = null,
        IReadOnlyList<StoredExtensionChildRow>? storedExtensionChildRows = null,
        IReadOnlyList<RequestExtensionChildItem>? requestExtensionChildItems = null
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

    protected static RequestParentItem RequestParent() => new(ParentCode, ArrayIndex: 0);

    protected static StoredParentRow StoredParent() => new(ParentCode, []);

    protected static RequestAlignedScope RequestAligned(ProfileVisibilityKind visibility, bool creatable) =>
        new(ParentCode, visibility, creatable);

    protected static StoredAlignedScope StoredAligned(ProfileVisibilityKind visibility) =>
        new(ParentCode, visibility, []);

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
    private IReadOnlyList<ParentRow> _parentRows = null!;
    private AlignedRow? _alignedRow;
    private long _alignedChildRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Created Parent", new AlignedInput("CreatedVisible", "CreatedHidden"))
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
            .Match<ParentRow>(row =>
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
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Created Parent")
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
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Hidden Request Parent",
                new AlignedInput("HiddenVisible", "HiddenHidden")
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
    private AlignedRow? _alignedRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Seed Parent", new AlignedInput("SeedVisible", "SeedHidden"))
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-visible-present-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Updated Parent", new AlignedInput("UpdatedVisible", "UpdatedHidden"))
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
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Seed Parent", new AlignedInput("SeedVisible", "SeedHidden"))
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-visible-absent-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Updated Parent")
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
    private AlignedRow? _alignedRow;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Seed Parent", new AlignedInput("StoredVisible", "StoredHidden"))
        );
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-hidden-storage-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(ParentCode, "Updated Parent")
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
    private IReadOnlyList<ParentRow> _parentRowsAfterPut = null!;
    private int _alignedRowCount;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(ParentResourceId, new ParentInput(ParentCode, "Seed Parent"));
        await SeedAsync(seedBody, "pgsql-profile-collection-aligned-create-denied-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Rejected Parent",
                new AlignedInput("RejectedVisible", "RejectedHidden")
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
// "aligned"."ParentResourceExtensionParentChildren" so the aligned-extension-child runtime
// is exercised on PostgreSQL as well as MSSQL.

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_a_Postgresql_ProfileCollectionAlignedExtension_create_request_with_aligned_extension_child_collection_items
    : PostgresqlProfileCollectionAlignedExtensionFixtureBase
{
    private UpsertResult _postResult = null!;
    private IReadOnlyList<AlignedChildRow> _childRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Created Parent",
                new AlignedInput(
                    "CreatedVisible",
                    "CreatedHidden",
                    Children:
                    [
                        new AlignedChildInput("ChildA", "ValueA"),
                        new AlignedChildInput("ChildB", "ValueB"),
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
                new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0),
                new RequestAlignedChildItem(ParentCode, "ChildB", ParentArrayIndex: 0, ChildArrayIndex: 1),
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
    private IReadOnlyList<AlignedChildRow> _childRowsBeforePut = null!;
    private IReadOnlyList<AlignedChildRow> _childRowsAfterPut = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Seed Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput("ChildA", "OriginalA"),
                        new AlignedChildInput("ChildB", "OriginalB"),
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

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Updated Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput("ChildA", "OriginalA"),
                        new AlignedChildInput("ChildB", "ChangedB"),
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
                new StoredAlignedChildRow(ParentCode, "ChildA", []),
                new StoredAlignedChildRow(ParentCode, "ChildB", []),
            ],
            [
                new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0),
                new RequestAlignedChildItem(ParentCode, "ChildB", ParentArrayIndex: 0, ChildArrayIndex: 1),
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
    private IReadOnlyList<AlignedChildRow> _childRowsAfterPut = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Seed Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput("ChildA", "ValueA"),
                        new AlignedChildInput("ChildB", "ValueB"),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-aligned-extension-child-delete-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Updated Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children: [new AlignedChildInput("ChildA", "ValueA")]
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
                new StoredAlignedChildRow(ParentCode, "ChildA", []),
                new StoredAlignedChildRow(ParentCode, "ChildB", []),
            ],
            [new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0)]
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
    private IReadOnlyList<AlignedChildRow> _childRowsBeforePut = null!;
    private IReadOnlyList<AlignedChildRow> _childRowsAfterPut = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Seed Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput("ChildA", "ValueA"),
                        new AlignedChildInput("ChildB", "ValueB"),
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

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Updated Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput("ChildB", "ValueB"),
                        new AlignedChildInput("ChildA", "ValueA"),
                        new AlignedChildInput("ChildC", "ValueC"),
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
                new StoredAlignedChildRow(ParentCode, "ChildA", []),
                new StoredAlignedChildRow(ParentCode, "ChildB", []),
            ],
            [
                new RequestAlignedChildItem(ParentCode, "ChildB", ParentArrayIndex: 0, ChildArrayIndex: 0),
                new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 1),
                new RequestAlignedChildItem(ParentCode, "ChildC", ParentArrayIndex: 0, ChildArrayIndex: 2),
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
    private IReadOnlyList<ExtensionChildRow> _extensionChildRows = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Created Parent",
                new AlignedInput(
                    "CreatedVisible",
                    "CreatedHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new ExtensionChildInput("ExtChildAlpha", "AlphaValue"),
                                new ExtensionChildInput("ExtChildBeta", "BetaValue"),
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
                new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0),
            ],
            requestExtensionChildItems:
            [
                new RequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
                new RequestExtensionChildItem(
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
    private IReadOnlyList<ExtensionChildRow> _extensionChildRowsBeforePut = null!;
    private IReadOnlyList<ExtensionChildRow> _extensionChildRowsAfterPut = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Seed Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new ExtensionChildInput("ExtChildAlpha", "OriginalAlpha"),
                                new ExtensionChildInput("ExtChildBeta", "OriginalBeta"),
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

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Updated Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new ExtensionChildInput("ExtChildAlpha", "OriginalAlpha"),
                                new ExtensionChildInput("ExtChildBeta", "ChangedBeta"),
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
            [new StoredAlignedChildRow(ParentCode, "ChildA", [])],
            [new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0)],
            [
                new StoredExtensionChildRow(ParentCode, "ChildA", "ExtChildAlpha", []),
                new StoredExtensionChildRow(ParentCode, "ChildA", "ExtChildBeta", []),
            ],
            [
                new RequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
                new RequestExtensionChildItem(
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
    private IReadOnlyList<ExtensionChildRow> _extensionChildRowsAfterPut = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Seed Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new ExtensionChildInput("ExtChildAlpha", "AlphaValue"),
                                new ExtensionChildInput("ExtChildBeta", "BetaValue"),
                            ]
                        ),
                    ]
                )
            )
        );
        await SeedAsync(seedBody, "postgres-profile-collection-nested-extension-child-delete-seed");

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Updated Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren: [new ExtensionChildInput("ExtChildAlpha", "AlphaValue")]
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
            [new StoredAlignedChildRow(ParentCode, "ChildA", [])],
            [new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0)],
            [
                new StoredExtensionChildRow(ParentCode, "ChildA", "ExtChildAlpha", []),
                new StoredExtensionChildRow(ParentCode, "ChildA", "ExtChildBeta", []),
            ],
            [
                new RequestExtensionChildItem(
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
    private IReadOnlyList<ExtensionChildRow> _extensionChildRowsBeforePut = null!;
    private IReadOnlyList<ExtensionChildRow> _extensionChildRowsAfterPut = null!;

    [OneTimeSetUp]
    public async Task ScenarioOneTimeSetUp()
    {
        var seedBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Seed Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new ExtensionChildInput("ExtChildAlpha", "AlphaValue"),
                                new ExtensionChildInput("ExtChildBeta", "BetaValue"),
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

        var writeBody = CreateParentResourceBody(
            ParentResourceId,
            new ParentInput(
                ParentCode,
                "Updated Parent",
                new AlignedInput(
                    "StoredVisible",
                    "StoredHidden",
                    Children:
                    [
                        new AlignedChildInput(
                            "ChildA",
                            "ValueA",
                            ExtensionChildren:
                            [
                                new ExtensionChildInput("ExtChildBeta", "BetaValue"),
                                new ExtensionChildInput("ExtChildAlpha", "AlphaValue"),
                                new ExtensionChildInput("ExtChildGamma", "GammaValue"),
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
            [new StoredAlignedChildRow(ParentCode, "ChildA", [])],
            [new RequestAlignedChildItem(ParentCode, "ChildA", ParentArrayIndex: 0, ChildArrayIndex: 0)],
            [
                new StoredExtensionChildRow(ParentCode, "ChildA", "ExtChildAlpha", []),
                new StoredExtensionChildRow(ParentCode, "ChildA", "ExtChildBeta", []),
            ],
            [
                new RequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildBeta",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 0
                ),
                new RequestExtensionChildItem(
                    ParentCode,
                    "ChildA",
                    "ExtChildAlpha",
                    ParentArrayIndex: 0,
                    ChildArrayIndex: 0,
                    ExtensionChildArrayIndex: 1
                ),
                new RequestExtensionChildItem(
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
