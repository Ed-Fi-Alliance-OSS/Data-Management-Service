// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// MSSQL twin of PostgresqlProfileSeparateTableMergeTests. See the pgsql sibling file header
// for the resource-choice rationale and the full narrative; this file differs only in the
// harness plumbing (MssqlGeneratedDdlTestDatabase, SqlParameter, bracketed identifiers).

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

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlProfileSeparateTableMergeAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileSeparateTableMergeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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
/// MSSQL twin of <c>ProfileSeparateTableMergeProjectionInvoker</c> — emits the root scope
/// plus the optional <c>$._ext.sample</c> separate-table scope with caller-supplied visibility
/// and hidden-member-path shapes.
/// </summary>
internal sealed class MssqlProfileSeparateTableMergeProjectionInvoker : IStoredStateProjectionInvoker
{
    private readonly ProfileVisibilityKind _rootVisibility;
    private readonly ImmutableArray<string> _rootHiddenMemberPaths;
    private readonly bool _emitExtScope;
    private readonly ProfileVisibilityKind _extScopeVisibility;
    private readonly ImmutableArray<string> _extScopeHiddenMemberPaths;

    public MssqlProfileSeparateTableMergeProjectionInvoker(
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

internal static class MssqlProfileSeparateTableMergeSupport
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
            StoredStateProjectionInvoker: new MssqlProfileSeparateTableMergeProjectionInvoker(
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
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileSeparateTableMerge",
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
            UpdateCascadeHandler: new MssqlProfileSeparateTableMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileSeparateTableMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileSeparateTableMerge",
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
            UpdateCascadeHandler: new MssqlProfileSeparateTableMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileSeparateTableMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<UpsertResult> ExecuteProfiledPostAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileSeparateTableMerge",
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
            UpdateCascadeHandler: new MssqlProfileSeparateTableMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileSeparateTableMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadRootRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT i.[ProfileSeparateTableMergeItemId], i.[DisplayName], i.[DocumentId]
            FROM [edfi].[ProfileSeparateTableMergeItem] i
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = i.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }

    public static async Task<int> CountExtRowsAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var scalar = await database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [sample].[ProfileSeparateTableMergeItemExtension] ext
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ext.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
        return scalar;
    }

    public static async Task<IReadOnlyDictionary<string, object?>?> TryReadExtRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                ext.[ExtVisibleScalar],
                ext.[ExtHiddenScalar],
                ext.[SampleCategoryDescriptor_DescriptorId],
                ext.[DocumentId]
            FROM [sample].[ProfileSeparateTableMergeItemExtension] ext
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ext.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Direct-SQL helpers for the slice 3 hidden-descriptor-FK preservation fixture (MSSQL
    /// twin of the pgsql sibling). The standard <see cref="SeedAsync"/> path does not thread
    /// descriptor references through the relational write without caller-provided
    /// <see cref="DescriptorReference"/> entries; for this one preservation case we seed the
    /// descriptor row and set the FK column on the pre-existing extension row directly.
    /// </summary>
    public static async Task<short> GetResourceKeyIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        string projectName,
        string resourceName
    )
    {
        return await database.ExecuteScalarAsync<short>(
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

    public static async Task<long> InsertDocumentRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        short resourceKeyId
    )
    {
        // dms.Document has the TR_Document_Journal trigger; OUTPUT without INTO is rejected
        // in that case, so route the inserted DocumentId through a table variable.
        return await database.ExecuteScalarAsync<long>(
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

    public static async Task InsertReferentialIdentityRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (
                SELECT 1 FROM [dms].[ReferentialIdentity] WHERE [ReferentialId] = @referentialId
            )
            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", referentialId),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    public static async Task<long> SeedSchoolTypeDescriptorAsync(
        MssqlGeneratedDdlTestDatabase database,
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
                @shortDescription,
                @discriminator,
                @uri
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
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
    /// inserted via <see cref="SeedAsync"/>.
    /// </summary>
    public static async Task SetExtRowSampleCategoryDescriptorAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid,
        long descriptorDocumentId
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            UPDATE ext
            SET [SampleCategoryDescriptor_DescriptorId] = @descriptorDocumentId
            FROM [sample].[ProfileSeparateTableMergeItemExtension] ext
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = ext.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value),
            new SqlParameter("@descriptorDocumentId", descriptorDocumentId)
        );
    }
}
