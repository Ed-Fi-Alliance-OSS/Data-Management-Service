// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Resource choice for Slice 3 profile merge integration tests:
//   ProfileSeparateTableMergeItem (from the Ed-Fi.DataManagementService.Backend.Ddl.Tests.Unit
//   small/profile-separate-table-merge fixture). A synthetic two-project fixture built for
//   this slice: a single root resource with a displayName scalar plus a Sample extension that
//   contributes one root-attached `$._ext.sample` RootExtension row carrying two scalars
//   (extVisibleScalar, extHiddenScalar) AND a SchoolTypeDescriptor FK binding
//   (sampleCategoryDescriptor → SampleCategoryDescriptor_DescriptorId). No collections, no
//   CollectionExtensionScope tables, no key unification — slice 3's separate-table persister
//   path touches only the root and the RootExtension row, which is exactly what the six
//   scenarios below exercise.
//
//   The existing small/ext fixture (School + SchoolExtension) exposes the _ext row shape,
//   but SchoolExtension has a single non-identity scalar (ExtensionData) which cannot
//   simultaneously host a visible and a hidden binding on the same _ext row — a shape each
//   of the hidden-binding / hidden-whole-scope scenarios requires. A synthetic fixture is
//   also the pattern slice 2 adopted (small/profile-root-only-merge) for the same reason:
//   precise control over bindings keeps the integration assertions load-bearing. The
//   descriptor FK on the extension scope satisfies the spec's call for "one separate-table
//   hidden-binding preservation case covering FK/descriptor or key-unification/synthetic-
//   presence behavior outside the root row"
//   (reference/design/backend-redesign/epics/07-relational-write-path/
//    03b-profile-aware-persist-executor/03-separate-table-profile-merge.md:119).
//
// This file carries the per-test infrastructure (file-scoped DI doubles, resource info,
// seed helpers, scope-state projection invoker). The sibling
// PostgresqlProfileSeparateTableMergeFixtureTests.cs carries the actual test fixtures.

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

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class ProfileSeparateTableMergeNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ProfileSeparateTableMergeAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ProfileSeparateTableMergeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

/// <summary>
/// Test-configurable <see cref="IStoredStateProjectionInvoker"/> for Slice 3 separate-table
/// fixtures. Emits the root scope <c>$</c> plus the separate-table scope <c>$._ext.sample</c>
/// with caller-supplied visibility and hidden-member-path shapes. Slice 3 drives decisions
/// from these stored scope states (as well as the matching request scope states).
/// </summary>
internal sealed class ProfileSeparateTableMergeProjectionInvoker : IStoredStateProjectionInvoker
{
    private readonly ProfileVisibilityKind _rootVisibility;
    private readonly ImmutableArray<string> _rootHiddenMemberPaths;
    private readonly bool _emitExtScope;
    private readonly ProfileVisibilityKind _extScopeVisibility;
    private readonly ImmutableArray<string> _extScopeHiddenMemberPaths;

    public ProfileSeparateTableMergeProjectionInvoker(
        ProfileVisibilityKind rootVisibility,
        ImmutableArray<string> rootHiddenMemberPaths,
        bool emitExtScope,
        ProfileVisibilityKind extScopeVisibility,
        ImmutableArray<string> extScopeHiddenMemberPaths
    )
    {
        _rootVisibility = rootVisibility;
        _rootHiddenMemberPaths = rootHiddenMemberPaths;
        _emitExtScope = emitExtScope;
        _extScopeVisibility = extScopeVisibility;
        _extScopeHiddenMemberPaths = extScopeHiddenMemberPaths;
    }

    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var storedScopeStates = new List<StoredScopeState>
        {
            new(
                Address: new ScopeInstanceAddress("$", []),
                Visibility: _rootVisibility,
                HiddenMemberPaths: _rootHiddenMemberPaths
            ),
        };

        if (_emitExtScope)
        {
            storedScopeStates.Add(
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: _extScopeVisibility,
                    HiddenMemberPaths: _extScopeHiddenMemberPaths
                )
            );
        }

        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates: [.. storedScopeStates],
            VisibleStoredCollectionRows: []
        );
    }
}

/// <summary>
/// Shared helpers for the Slice 3 separate-table profile merge integration tests against the
/// synthetic ProfileSeparateTableMergeItem fixture.
/// </summary>
internal static class PostgresqlProfileSeparateTableMergeSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-separate-table-merge";

    public static readonly QualifiedResourceName ItemResource = new("Ed-Fi", "ProfileSeparateTableMergeItem");

    public static readonly ResourceInfo ItemResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ProfileSeparateTableMergeItem"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public const string ExtScopeAddress = "$._ext.sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];
        services.AddSingleton<
            IHostApplicationLifetime,
            ProfileSeparateTableMergeNoOpHostApplicationLifetime
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

    public static DocumentInfo CreateDocumentInfo(int itemId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.profileSeparateTableMergeItemId"),
                itemId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(ItemResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    /// <summary>
    /// Builds a BackendProfileWriteContext with both the root and the separate-table
    /// <c>$._ext.sample</c> scope states. Pass <paramref name="emitExtRequestScope"/> false to
    /// omit the separate-table request scope entirely (callers rarely need this outside of
    /// negative contract scenarios; the default keeps both scopes in the request so slice 3
    /// routes the separate-table path).
    /// </summary>
    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        ProfileVisibilityKind rootVisibility,
        ImmutableArray<string> rootHiddenMemberPaths,
        bool emitExtRequestScope,
        ProfileVisibilityKind extRequestVisibility,
        bool extCreatable,
        bool emitExtStoredScope,
        ProfileVisibilityKind extStoredVisibility,
        ImmutableArray<string> extStoredHiddenMemberPaths,
        bool rootCreatable = true,
        string profileName = "separate-table-merge-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        var requestScopeStates = new List<RequestScopeState>
        {
            new(
                Address: new ScopeInstanceAddress("$", []),
                Visibility: rootVisibility,
                Creatable: rootCreatable
            ),
        };

        if (emitExtRequestScope)
        {
            requestScopeStates.Add(
                new RequestScopeState(
                    Address: new ScopeInstanceAddress(ExtScopeAddress, []),
                    Visibility: extRequestVisibility,
                    Creatable: extCreatable
                )
            );
        }

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates: [.. requestScopeStates],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileSeparateTableMergeProjectionInvoker(
                rootVisibility,
                rootHiddenMemberPaths,
                emitExtStoredScope,
                extStoredVisibility,
                extStoredHiddenMemberPaths
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int itemId,
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
                    InstanceName: "PostgresqlProfileSeparateTableMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: ItemResourceInfo,
            DocumentInfo: CreateDocumentInfo(itemId),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileSeparateTableMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileSeparateTableMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int itemId,
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
                    InstanceName: "PostgresqlProfileSeparateTableMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
        var updateRequest = new UpdateRequest(
            ResourceInfo: ItemResourceInfo,
            DocumentInfo: CreateDocumentInfo(itemId),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileSeparateTableMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileSeparateTableMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<UpsertResult> ExecuteProfiledPostAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int itemId,
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
                    InstanceName: "PostgresqlProfileSeparateTableMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: ItemResourceInfo,
            DocumentInfo: CreateDocumentInfo(itemId),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ProfileSeparateTableMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileSeparateTableMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadRootRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT i."ProfileSeparateTableMergeItemId", i."DisplayName", i."DocumentId"
            FROM "edfi"."ProfileSeparateTableMergeItem" i
            INNER JOIN "dms"."Document" d ON d."DocumentId" = i."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }

    public static async Task<int> CountExtRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var scalar = await database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "sample"."ProfileSeparateTableMergeItemExtension" ext
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ext."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
        return (int)scalar;
    }

    public static async Task<IReadOnlyDictionary<string, object?>?> TryReadExtRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ext."ExtVisibleScalar",
                ext."ExtHiddenScalar",
                ext."SampleCategoryDescriptor_DescriptorId",
                ext."DocumentId"
            FROM "sample"."ProfileSeparateTableMergeItemExtension" ext
            INNER JOIN "dms"."Document" d ON d."DocumentId" = ext."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Direct-SQL helpers for the slice 3 hidden-descriptor-FK preservation fixture. The
    /// standard <see cref="SeedAsync"/> path does not know how to thread descriptor references
    /// through the relational write without caller-provided <see cref="DescriptorReference"/>
    /// entries; for this one preservation case we seed the descriptor row and set the FK
    /// column on the pre-existing extension row directly, matching the pattern used in
    /// PostgresqlProfileRootTableOnlyMergeFixtureTests.
    /// </summary>
    public static async Task<short> GetResourceKeyIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string projectName,
        string resourceName
    )
    {
        return await database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    public static async Task<long> InsertDocumentRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        short resourceKeyId
    )
    {
        return await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    public static async Task InsertReferentialIdentityRowAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    public static async Task<long> SeedSchoolTypeDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "SchoolTypeDescriptor");
        var documentId = await InsertDocumentRowAsync(database, documentUuid, resourceKeyId);
        var uri = $"{@namespace}#{codeValue}";
        const string discriminator = "Ed-Fi:SchoolTypeDescriptor";
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @shortDescription,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("SchoolTypeDescriptor"),
            true
        );
        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri.ToLowerInvariant()),
        ]);
        var referentialId = ReferentialIdCalculator.ReferentialIdFrom(
            descriptorResourceInfo,
            descriptorIdentity
        );
        await InsertReferentialIdentityRowAsync(database, referentialId.Value, documentId, resourceKeyId);

        return documentId;
    }

    /// <summary>
    /// Writes the descriptor FK column on the extension row after the root+ext row was already
    /// inserted via <see cref="SeedAsync"/>. Mirrors the direct-UPDATE seeding pattern used by
    /// the sibling MSSQL fixture.
    /// </summary>
    public static async Task SetExtRowSampleCategoryDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid,
        long descriptorDocumentId
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."ProfileSeparateTableMergeItemExtension" ext
            SET "SampleCategoryDescriptor_DescriptorId" = @descriptorDocumentId
            FROM "dms"."Document" d
            WHERE d."DocumentId" = ext."DocumentId"
              AND d."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value),
            new NpgsqlParameter("descriptorDocumentId", descriptorDocumentId)
        );
    }
}
