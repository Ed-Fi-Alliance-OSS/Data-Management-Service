// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// MSSQL mirror of PostgresqlProfileRootTableOnlyMergeFixtureTests. See the pgsql sibling
// for fixture rationale and the scenario mapping. Column names are identical across the two
// dialects so only the harness plumbing (MssqlGeneratedDdlTestDatabase, SqlParameter, bracketed
// identifiers) differs.

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

file sealed class MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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
/// MSSQL-flavored projection invoker; emits root scope plus an inlined
/// <c>$.profileScope</c> scope with caller-supplied visibility/hidden-member-paths.
/// </summary>
internal sealed class MssqlProfileRootOnlyFixtureProjectionInvoker : IStoredStateProjectionInvoker
{
    private readonly ProfileVisibilityKind _rootVisibility;
    private readonly System.Collections.Immutable.ImmutableArray<string> _rootHiddenMemberPaths;
    private readonly ProfileVisibilityKind _profileScopeVisibility;
    private readonly System.Collections.Immutable.ImmutableArray<string> _profileScopeHiddenMemberPaths;

    public MssqlProfileRootOnlyFixtureProjectionInvoker(
        ProfileVisibilityKind rootVisibility,
        System.Collections.Immutable.ImmutableArray<string> rootHiddenMemberPaths,
        ProfileVisibilityKind profileScopeVisibility,
        System.Collections.Immutable.ImmutableArray<string> profileScopeHiddenMemberPaths
    )
    {
        _rootVisibility = rootVisibility;
        _rootHiddenMemberPaths = rootHiddenMemberPaths;
        _profileScopeVisibility = profileScopeVisibility;
        _profileScopeHiddenMemberPaths = profileScopeHiddenMemberPaths;
    }

    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var rootAddress = new ScopeInstanceAddress("$", []);
        var profileScopeAddress = new ScopeInstanceAddress("$.profileScope", []);
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: rootAddress,
                    Visibility: _rootVisibility,
                    HiddenMemberPaths: _rootHiddenMemberPaths
                ),
                new StoredScopeState(
                    Address: profileScopeAddress,
                    Visibility: _profileScopeVisibility,
                    HiddenMemberPaths: _profileScopeHiddenMemberPaths
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
}

internal static class MssqlProfileRootOnlyFixtureSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge";

    public static readonly QualifiedResourceName ProfileRootOnlyMergeItemResource = new(
        "Ed-Fi",
        "ProfileRootOnlyMergeItem"
    );

    public static readonly ResourceInfo ProfileRootOnlyMergeItemResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ProfileRootOnlyMergeItem"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static readonly IReadOnlyList<(string JsonScope, ScopeKind Kind)> InlinedScopes =
    [
        ("$.profileScope", ScopeKind.NonCollection),
    ];

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

    public static DocumentInfo CreateDocumentInfo(int profileRootOnlyMergeItemId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.profileRootOnlyMergeItemId"),
                profileRootOnlyMergeItemId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                ProfileRootOnlyMergeItemResourceInfo,
                identity
            ),
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
        System.Collections.Immutable.ImmutableArray<string> rootHiddenMemberPaths,
        ProfileVisibilityKind profileScopeVisibility,
        System.Collections.Immutable.ImmutableArray<string> profileScopeHiddenMemberPaths,
        bool creatable = true,
        string profileName = "root-only-merge-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan, InlinedScopes);
        RequestScopeState[] scopeStates =
        [
            new RequestScopeState(
                Address: new ScopeInstanceAddress("$", []),
                Visibility: rootVisibility,
                Creatable: creatable
            ),
            new RequestScopeState(
                Address: new ScopeInstanceAddress("$.profileScope", []),
                Visibility: profileScopeVisibility,
                Creatable: creatable
            ),
        ];
        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: creatable,
                RequestScopeStates: [.. scopeStates],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlProfileRootOnlyFixtureProjectionInvoker(
                rootVisibility,
                rootHiddenMemberPaths,
                profileScopeVisibility,
                profileScopeHiddenMemberPaths
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        int profileRootOnlyMergeItemId,
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
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
        var upsertRequest = new UpsertRequest(
            ResourceInfo: ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: CreateDocumentInfo(profileRootOnlyMergeItemId),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadItemRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                i.[ProfileRootOnlyMergeItemId],
                i.[DisplayName],
                i.[ProfileScopeClearableText],
                i.[ProfileScopePreservedText]
            FROM [edfi].[ProfileRootOnlyMergeItem] i
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = i.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prerequisite-seed helpers (MSSQL dialect). Mirror of the pgsql sibling
    //  file; dialect differences: bracket identifiers, SqlParameter, bit (0/1)
    //  for boolean, OUTPUT INSERTED for scalar return. See pgsql file header
    //  comment for rationale + trigger behavior; triggers on edfi.Student and
    //  edfi.ProfileRootOnlyMergeItem manage dms.ReferentialIdentity for their
    //  own rows, but descriptors need manual insertion.
    // ─────────────────────────────────────────────────────────────────────────

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

    public static async Task<long> SeedStudentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        string studentUniqueId,
        string firstName = "Seed"
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "Student");
        var documentId = await InsertDocumentRowAsync(database, documentUuid, resourceKeyId);
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName])
            VALUES (@documentId, @studentUniqueId, @firstName);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@firstName", firstName)
        );
        return documentId;
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

    public static async Task<long> SeedProfileRootOnlyMergeItemRowAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        int profileRootOnlyMergeItemId,
        string displayName,
        string? clearableText,
        string? preservedText,
        long? studentDocumentId,
        string? studentUniqueId,
        long? unifiedDescriptorId,
        bool primaryPresent,
        bool secondaryPresent
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "ProfileRootOnlyMergeItem");
        var documentId = await InsertDocumentRowAsync(database, documentUuid, resourceKeyId);

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[ProfileRootOnlyMergeItem] (
                [DocumentId],
                [ProfileRootOnlyMergeItemId],
                [DisplayName],
                [ProfileScopeClearableText],
                [ProfileScopePreservedText],
                [StudentReference_DocumentId],
                [StudentReference_StudentUniqueId],
                [PrimarySchoolTypeDescriptor_DescriptorId_Present],
                [SecondarySchoolTypeDescriptor_DescriptorId_Present],
                [PrimarySchoolTypeDescriptor_Unified_DescriptorId]
            )
            VALUES (
                @documentId,
                @profileRootOnlyMergeItemId,
                @displayName,
                @clearableText,
                @preservedText,
                @studentDocumentId,
                @studentUniqueId,
                @primaryPresent,
                @secondaryPresent,
                @unifiedDescriptorId
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@profileRootOnlyMergeItemId", profileRootOnlyMergeItemId),
            new SqlParameter("@displayName", (object?)displayName ?? DBNull.Value),
            new SqlParameter("@clearableText", (object?)clearableText ?? DBNull.Value),
            new SqlParameter("@preservedText", (object?)preservedText ?? DBNull.Value),
            new SqlParameter("@studentDocumentId", (object?)studentDocumentId ?? DBNull.Value),
            new SqlParameter("@studentUniqueId", (object?)studentUniqueId ?? DBNull.Value),
            new SqlParameter("@primaryPresent", primaryPresent ? (object)(byte)1 : DBNull.Value),
            new SqlParameter("@secondaryPresent", secondaryPresent ? (object)(byte)1 : DBNull.Value),
            new SqlParameter("@unifiedDescriptorId", (object?)unifiedDescriptorId ?? DBNull.Value)
        );

        return documentId;
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ReadItemRowWithReferenceColumnsAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT
                i.[ProfileRootOnlyMergeItemId],
                i.[DisplayName],
                i.[ProfileScopeClearableText],
                i.[ProfileScopePreservedText],
                i.[StudentReference_DocumentId],
                i.[StudentReference_StudentUniqueId],
                i.[PrimarySchoolTypeDescriptor_DescriptorId_Present],
                i.[SecondarySchoolTypeDescriptor_DescriptorId_Present],
                i.[PrimarySchoolTypeDescriptor_Unified_DescriptorId]
            FROM [edfi].[ProfileRootOnlyMergeItem] i
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = i.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
        rows.Should().HaveCount(1);
        return rows[0];
    }

    public static DocumentInfo CreateDocumentInfoWithReferences(
        int profileRootOnlyMergeItemId,
        DocumentReference[] documentReferences,
        DescriptorReference[] descriptorReferences
    )
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.profileRootOnlyMergeItemId"),
                profileRootOnlyMergeItemId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                ProfileRootOnlyMergeItemResourceInfo,
                identity
            ),
            DocumentReferences: documentReferences,
            DocumentReferenceArrays: [],
            DescriptorReferences: descriptorReferences,
            SuperclassIdentity: null
        );
    }

    public static DescriptorReference BuildSchoolTypeDescriptorReference(string uri, string referenceJsonPath)
    {
        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("SchoolTypeDescriptor"),
            true
        );
        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri.ToLowerInvariant()),
        ]);
        return new DescriptorReference(
            ResourceInfo: descriptorResourceInfo,
            DocumentIdentity: descriptorIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                descriptorResourceInfo,
                descriptorIdentity
            ),
            Path: new JsonPath(referenceJsonPath)
        );
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture A: Hidden inlined preservation on the root scope.
//  Spec scenario 5 — hidden inlined column preservation, multi-column.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Put_With_Hidden_Inlined_PreservedText_On_Root_Scope
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("bb100001-0000-0000-0000-000000000001")
    );
    private const int ItemId = 9101;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = "OriginalClearable",
                ["preservedText"] = "OriginalPreserved",
            },
        };
        var seedResult = await MssqlProfileRootOnlyFixtureSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-profile-root-only-hidden-inlined-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["profileScope"] = new JsonObject { ["clearableText"] = "UpdatedClearable" },
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowAsync(_database, DocumentUuid);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_updates_profile_scope_clearable_text() =>
        _rowAfterPut["ProfileScopeClearableText"].Should().Be("UpdatedClearable");

    [Test]
    public void It_preserves_profile_scope_preserved_text() =>
        _rowAfterPut["ProfileScopePreservedText"].Should().Be("OriginalPreserved");

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: ["preservedText"]
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(ItemId),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-hidden-inlined-put"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture B: Visible-absent inlined scope clears clearable, preserves hidden.
//  Spec scenario 4 — visible-absent inlined scope clears clearable only, with
//  hidden-override precedence protecting preservedText.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_Put_With_VisibleAbsent_Inlined_Scope_Clears_Clearable_And_Preserves_Hidden
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("bb100002-0000-0000-0000-000000000002")
    );
    private const int ItemId = 9102;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        var seedBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "OriginalDisplay",
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = "OriginalClearable",
                ["preservedText"] = "OriginalPreserved",
            },
        };
        var seedResult = await MssqlProfileRootOnlyFixtureSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            ItemId,
            seedBody,
            DocumentUuid,
            "mssql-profile-root-only-visible-absent-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowAsync(_database, DocumentUuid);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_clears_profile_scope_clearable_text() =>
        _rowAfterPut["ProfileScopeClearableText"].Should().BeNull();

    [Test]
    public void It_preserves_profile_scope_preserved_text() =>
        _rowAfterPut["ProfileScopePreservedText"].Should().Be("OriginalPreserved");

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: [],
            profileScopeVisibility: ProfileVisibilityKind.VisibleAbsent,
            profileScopeHiddenMemberPaths: ["preservedText"]
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfo(ItemId),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-visible-absent-put"),
            DocumentUuid: DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture C: Hidden StudentReference preservation (FK + propagated identity).
//  MSSQL mirror of the pgsql fixture covering spec scenario 6's FK half. The
//  "descriptor half" of scenario 6 folds into scenario 7a (Fixture D) via
//  symmetry — see the pgsql sibling's header comment for details.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_ProfiledRootOnly_HiddenStudentReference_PreservesFKAndPropagatedIdentity
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb100003-0000-0000-0000-000000000003");
    private static readonly Guid StudentDocumentUuid = Guid.Parse("bb100003-1000-0000-0000-000000000003");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb100003-2000-0000-0000-000000000003"
    );
    private const int ItemId = 9103;
    private const string StudentUniqueId = "STU-103";
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private static readonly string PublicUri = $"{DescriptorNamespace}#{PublicCodeValue}";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _studentDocumentId;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _studentDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedStudentAsync(
            _database,
            StudentDocumentUuid,
            StudentUniqueId
        );
        _publicDescriptorDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PublicDescriptorDocumentUuid,
            DescriptorNamespace,
            PublicCodeValue,
            PublicCodeValue
        );
        await MssqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: _studentDocumentId,
            studentUniqueId: StudentUniqueId,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PublicUri,
            ["secondarySchoolTypeDescriptor"] = PublicUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_student_reference_document_id() =>
        _rowAfterPut["StudentReference_DocumentId"].Should().Be(_studentDocumentId);

    [Test]
    public void It_preserves_student_reference_student_unique_id() =>
        _rowAfterPut["StudentReference_StudentUniqueId"].Should().Be(StudentUniqueId);

    [Test]
    public void It_preserves_unified_descriptor_canonical_fk() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    [Test]
    public void It_preserves_primary_descriptor_synthetic_presence() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    [Test]
    public void It_preserves_secondary_descriptor_synthetic_presence() =>
        _rowAfterPut["SecondarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["studentReference"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var descriptorReferences = new[]
        {
            MssqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.primarySchoolTypeDescriptor"
            ),
            MssqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.secondarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-hidden-ref-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture C.1: Hidden *sub-reference* member path preserves FK + propagated identity.
//  MSSQL mirror of the pgsql Fixture C.1. Regression for profile hidden-reference
//  governance (profiles.md:782): hiding a sub-reference path must preserve the FK
//  column and every propagated identity binding as a single reference-derived group.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_ProfiledRootOnly_HiddenSubReferenceMember_PreservesFKAndPropagatedIdentity
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb100004-0000-0000-0000-000000000004");
    private static readonly Guid StudentDocumentUuid = Guid.Parse("bb100004-1000-0000-0000-000000000004");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb100004-2000-0000-0000-000000000004"
    );
    private const int ItemId = 9104;
    private const string StudentUniqueId = "STU-104";
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private static readonly string PublicUri = $"{DescriptorNamespace}#{PublicCodeValue}";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _studentDocumentId;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _studentDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedStudentAsync(
            _database,
            StudentDocumentUuid,
            StudentUniqueId
        );
        _publicDescriptorDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PublicDescriptorDocumentUuid,
            DescriptorNamespace,
            PublicCodeValue,
            PublicCodeValue
        );
        await MssqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: _studentDocumentId,
            studentUniqueId: StudentUniqueId,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PublicUri,
            ["secondarySchoolTypeDescriptor"] = PublicUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_student_reference_document_id() =>
        _rowAfterPut["StudentReference_DocumentId"].Should().Be(_studentDocumentId);

    [Test]
    public void It_preserves_student_reference_student_unique_id() =>
        _rowAfterPut["StudentReference_StudentUniqueId"].Should().Be(StudentUniqueId);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixtureSubRefHidden",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["studentReference.studentUniqueId"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var descriptorReferences = new[]
        {
            MssqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.primarySchoolTypeDescriptor"
            ),
            MssqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.secondarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-hidden-sub-ref-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture D: Key-unification hidden member + visible agreement.
//  MSSQL mirror of scenario 7a.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_ProfiledRootOnly_KeyUnificationHiddenMember_AgreementSucceeds
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb100004-0000-0000-0000-000000000004");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb100004-2000-0000-0000-000000000004"
    );
    private const int ItemId = 9104;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private static readonly string PublicUri = $"{DescriptorNamespace}#{PublicCodeValue}";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _publicDescriptorDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PublicDescriptorDocumentUuid,
            DescriptorNamespace,
            PublicCodeValue,
            PublicCodeValue
        );
        await MssqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: null,
            studentUniqueId: null,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PublicUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_unified_descriptor_canonical_fk() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    [Test]
    public void It_preserves_primary_descriptor_synthetic_presence() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    [Test]
    public void It_preserves_secondary_descriptor_synthetic_presence() =>
        _rowAfterPut["SecondarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["secondarySchoolTypeDescriptor"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var descriptorReferences = new[]
        {
            MssqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PublicUri,
                "$.primarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-ku-agreement-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture E: Key-unification hidden member + visible DISagreement.
//  MSSQL mirror of scenario 7b.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_ProfiledRootOnly_KeyUnificationHiddenMember_DisagreementFailsValidation
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb100005-0000-0000-0000-000000000005");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb100005-1000-0000-0000-000000000005"
    );
    private static readonly Guid PrivateDescriptorDocumentUuid = Guid.Parse(
        "bb100005-2000-0000-0000-000000000005"
    );
    private const int ItemId = 9105;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";
    private const string PrivateCodeValue = "Private";
    private static readonly string PrivateUri = $"{DescriptorNamespace}#{PrivateCodeValue}";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _publicDescriptorDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PublicDescriptorDocumentUuid,
            DescriptorNamespace,
            PublicCodeValue,
            PublicCodeValue
        );
        await MssqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PrivateDescriptorDocumentUuid,
            DescriptorNamespace,
            PrivateCodeValue,
            PrivateCodeValue
        );
        await MssqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: null,
            studentUniqueId: null,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
            ["primarySchoolTypeDescriptor"] = PrivateUri,
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_validation_failure() =>
        _putResult.Should().BeOfType<UpdateResult.UpdateFailureValidation>();

    [Test]
    public void It_surfaces_key_unification_conflict_message()
    {
        var failure = _putResult as UpdateResult.UpdateFailureValidation;
        failure.Should().NotBeNull();
        failure!
            .ValidationFailures.Should()
            .Contain(f => f.Message.Contains("Key-unification conflict", StringComparison.Ordinal));
    }

    [Test]
    public void It_leaves_display_name_unchanged() =>
        _rowAfterPut["DisplayName"].Should().Be("OriginalDisplay");

    [Test]
    public void It_leaves_unified_descriptor_canonical_fk_unchanged() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["secondarySchoolTypeDescriptor"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var descriptorReferences = new[]
        {
            MssqlProfileRootOnlyFixtureSupport.BuildSchoolTypeDescriptorReference(
                PrivateUri,
                "$.primarySchoolTypeDescriptor"
            ),
        };
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: descriptorReferences
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-ku-disagreement-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fixture F: Synthetic presence preservation when both k-u members are hidden.
//  MSSQL mirror of scenario 8.
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_ProfiledRootOnly_SyntheticPresence_PreservedWhenGoverningMemberHidden
{
    private static readonly Guid ItemDocumentUuid = Guid.Parse("bb100006-0000-0000-0000-000000000006");
    private static readonly Guid PublicDescriptorDocumentUuid = Guid.Parse(
        "bb100006-2000-0000-0000-000000000006"
    );
    private const int ItemId = 9106;
    private const string DescriptorNamespace = "uri://ed-fi.org/SchoolTypeDescriptor";
    private const string PublicCodeValue = "Public";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _publicDescriptorDocumentId;
    private UpdateResult _putResult = null!;
    private IReadOnlyDictionary<string, object?> _rowAfterPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRootOnlyFixtureSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRootOnlyFixtureSupport.CreateServiceProvider();

        _publicDescriptorDocumentId = await MssqlProfileRootOnlyFixtureSupport.SeedSchoolTypeDescriptorAsync(
            _database,
            PublicDescriptorDocumentUuid,
            DescriptorNamespace,
            PublicCodeValue,
            PublicCodeValue
        );
        await MssqlProfileRootOnlyFixtureSupport.SeedProfileRootOnlyMergeItemRowAsync(
            _database,
            ItemDocumentUuid,
            ItemId,
            displayName: "OriginalDisplay",
            clearableText: null,
            preservedText: null,
            studentDocumentId: null,
            studentUniqueId: null,
            unifiedDescriptorId: _publicDescriptorDocumentId,
            primaryPresent: true,
            secondaryPresent: true
        );

        var writeBody = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = ItemId,
            ["displayName"] = "UpdatedDisplay",
        };
        _putResult = await ExecuteProfiledPutAsync(writeBody);
        _rowAfterPut = await MssqlProfileRootOnlyFixtureSupport.ReadItemRowWithReferenceColumnsAsync(
            _database,
            new DocumentUuid(ItemDocumentUuid)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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

    [Test]
    public void It_returns_update_success() => _putResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_updates_display_name() => _rowAfterPut["DisplayName"].Should().Be("UpdatedDisplay");

    [Test]
    public void It_preserves_unified_descriptor_canonical_fk() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_Unified_DescriptorId"]
            .Should()
            .Be(_publicDescriptorDocumentId);

    [Test]
    public void It_preserves_primary_descriptor_synthetic_presence() =>
        _rowAfterPut["PrimarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    [Test]
    public void It_preserves_secondary_descriptor_synthetic_presence() =>
        _rowAfterPut["SecondarySchoolTypeDescriptor_DescriptorId_Present"].Should().Be(true);

    private async Task<UpdateResult> ExecuteProfiledPutAsync(JsonNode writeBody)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfileRootOnlyFixture",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResource
        ];
        var profileContext = MssqlProfileRootOnlyFixtureSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            rootVisibility: ProfileVisibilityKind.VisiblePresent,
            rootHiddenMemberPaths: ["primarySchoolTypeDescriptor", "secondarySchoolTypeDescriptor"],
            profileScopeVisibility: ProfileVisibilityKind.VisiblePresent,
            profileScopeHiddenMemberPaths: []
        );
        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileRootOnlyFixtureSupport.ProfileRootOnlyMergeItemResourceInfo,
            DocumentInfo: MssqlProfileRootOnlyFixtureSupport.CreateDocumentInfoWithReferences(
                ItemId,
                documentReferences: [],
                descriptorReferences: []
            ),
            MappingSet: _mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId("mssql-profile-root-only-ku-both-hidden-put"),
            DocumentUuid: new DocumentUuid(ItemDocumentUuid),
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRootOnlyFixtureNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRootOnlyFixtureAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }
}
