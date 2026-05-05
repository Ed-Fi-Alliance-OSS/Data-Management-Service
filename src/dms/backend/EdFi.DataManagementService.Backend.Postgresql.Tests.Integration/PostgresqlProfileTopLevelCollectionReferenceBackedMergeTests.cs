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

// ---------------------------------------------------------------------------
// File-scoped no-op stubs (same pattern as the scalar-identity fixture)
// ---------------------------------------------------------------------------

file sealed class ReferenceBackedTopLevelCollectionNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class ReferenceBackedTopLevelCollectionAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ReferenceBackedTopLevelCollectionNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

// ---------------------------------------------------------------------------
// Projection invoker — always returns all rows visible with no hidden members
// ---------------------------------------------------------------------------

internal sealed class ReferenceBackedTopLevelCollectionProjectionInvoker(
    ImmutableArray<VisibleStoredCollectionRow> visibleStoredRows
) : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: visibleStoredRows
        );
    }
}

// ---------------------------------------------------------------------------
// Data record types
// ---------------------------------------------------------------------------

/// <summary>
/// A row read back from edfi.SchoolProgram for assertion purposes.
/// </summary>
internal sealed record SchoolProgramRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    int? ProgramReferenceDocumentId,
    int? ProgramReferenceProgramId,
    string? ProgramReferenceProgramName
);

/// <summary>
/// A request item: which program to include in the PUT body and whether it is creatable.
/// </summary>
internal sealed record ReferenceBackedRequestItem(int ProgramId, string ProgramName, bool Creatable);

/// <summary>
/// A visible stored row: which program was visible in the stored collection.
/// </summary>
internal sealed record ReferenceBackedStoredRow(
    int ProgramId,
    string ProgramName,
    ImmutableArray<string> HiddenMemberPaths
);

// ---------------------------------------------------------------------------
// Support class: DDL fixture, body builders, profile context builders, DB helpers
// ---------------------------------------------------------------------------

internal static class ReferenceBackedTopLevelCollectionMergeSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/top-level-reference-backed-collection";

    public const string ProgramsScope = "$.programs[*]";

    // ResourceKeyId values are fixed by the DDL seed: Program=1, School=2
    private const short ProgramResourceKeyId = 1;

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static readonly BaseResourceInfo ProgramBaseResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("Program"),
        false
    );

    public static readonly ResourceInfo ProgramResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Program"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    /// <summary>
    /// Build a minimal service provider wired for the test database.
    /// </summary>
    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            ReferenceBackedTopLevelCollectionNoOpHostApplicationLifetime
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

    // ── Body builders ────────────────────────────────────────────────────────

    /// <summary>Builds a School JSON body with zero or more program references.</summary>
    public static JsonNode CreateSchoolBody(
        long schoolId,
        params (int ProgramId, string ProgramName)[] programs
    )
    {
        JsonArray programNodes = [];
        foreach (var (programId, programName) in programs)
        {
            programNodes.Add(
                new JsonObject
                {
                    ["programReference"] = new JsonObject
                    {
                        ["programId"] = programId,
                        ["programName"] = programName,
                    },
                }
            );
        }

        return new JsonObject { ["schoolId"] = checked((int)schoolId), ["programs"] = programNodes };
    }

    /// <summary>Builds a DocumentInfo for a School, including DocumentReferences for each program.</summary>
    public static DocumentInfo CreateDocumentInfo(
        long schoolId,
        params (int ProgramId, string ProgramName)[] programs
    )
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                schoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        var programReferences = programs
            .Select((prog, index) => CreateProgramDocumentReference(prog.ProgramId, prog.ProgramName, index))
            .ToArray();

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, identity),
            DocumentReferences: programReferences,
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    /// <summary>
    /// Builds a DocumentReference for one program entry in a School's programs array.
    /// The path convention matches the extraction path: $.programs[{index}].programReference
    /// </summary>
    private static DocumentReference CreateProgramDocumentReference(
        int programId,
        string programName,
        int index
    )
    {
        var programIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.programId"),
                programId.ToString(CultureInfo.InvariantCulture)
            ),
            new DocumentIdentityElement(new JsonPath("$.programName"), programName),
        ]);

        return new DocumentReference(
            ResourceInfo: ProgramBaseResourceInfo,
            DocumentIdentity: programIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                ProgramBaseResourceInfo,
                programIdentity
            ),
            Path: new JsonPath($"$.programs[{index}].programReference")
        );
    }

    // ── Profile context builder ───────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="BackendProfileWriteContext"/> for a reference-backed programs collection.
    /// The semantic identity for each programs[*] item is (programReference.programId, programReference.programName).
    /// </summary>
    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<ReferenceBackedRequestItem> requestItems,
        IReadOnlyList<ReferenceBackedStoredRow> visibleStoredRows,
        bool rootCreatable = true,
        string profileName = "ref-backed-top-level-collection-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var programScopeDescriptor = scopeCatalog.Single(d => d.JsonScope == ProgramsScope);

        // The reference-fallback identity produces two paths in order:
        // [0] = "programReference.programId"
        // [1] = "programReference.programName"
        var identityPaths = programScopeDescriptor.SemanticIdentityRelativePathsInOrder;
        identityPaths
            .Should()
            .HaveCount(2, "reference-backed identity for Program has programId + programName");

        var programIdPath = identityPaths[0];
        var programNamePath = identityPaths[1];

        // Build visible request items
        var visibleRequestItems = requestItems
            .Select(
                (item, index) =>
                    new VisibleRequestCollectionItem(
                        CreateRowAddress(programIdPath, programNamePath, item.ProgramId, item.ProgramName),
                        item.Creatable,
                        $"$.programs[{index}]"
                    )
            )
            .ToImmutableArray();

        // Build visible stored rows
        var storedRows = visibleStoredRows
            .Select(row => new VisibleStoredCollectionRow(
                CreateRowAddress(programIdPath, programNamePath, row.ProgramId, row.ProgramName),
                row.HiddenMemberPaths
            ))
            .ToImmutableArray();

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: rootCreatable
                    ),
                ],
                VisibleRequestCollectionItems: visibleRequestItems
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ReferenceBackedTopLevelCollectionProjectionInvoker(storedRows)
        );
    }

    private static CollectionRowAddress CreateRowAddress(
        string programIdPath,
        string programNamePath,
        int programId,
        string programName
    ) =>
        new(
            ProgramsScope,
            new ScopeInstanceAddress("$", []),
            [
                new SemanticIdentityPart(programIdPath, JsonValue.Create(programId), IsPresent: true),
                new SemanticIdentityPart(programNamePath, JsonValue.Create(programName), IsPresent: true),
            ]
        );

    // ── Database operations ────────────────────────────────────────────────────

    private static void SetInstanceSelection(IServiceScope scope, PostgresqlGeneratedDdlTestDatabase database)
    {
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "ReferenceBackedTopLevelCollectionMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    /// <summary>
    /// Seeds a Program document directly via raw SQL so that dms.ReferentialIdentity is populated
    /// for subsequent School upserts that reference these programs.
    /// Returns the inserted DocumentId.
    /// </summary>
    public static async Task<long> SeedProgramAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        int programId,
        string programName
    )
    {
        var dbDocumentId = await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", ProgramResourceKeyId)
        );

        // Inserting into edfi.Program fires the TF_TR_Program_ReferentialIdentity trigger,
        // which automatically inserts the matching row into dms.ReferentialIdentity.
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Program" ("DocumentId", "ProgramId", "ProgramName")
            VALUES (@documentId, @programId, @programName);
            """,
            new NpgsqlParameter("documentId", dbDocumentId),
            new NpgsqlParameter("programId", programId),
            new NpgsqlParameter("programName", programName)
        );

        return dbDocumentId;
    }

    /// <summary>Seeds a School document (plain upsert, no profile) and returns the inserted rows.</summary>
    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        long schoolId,
        JsonNode body,
        DocumentUuid documentUuid,
        (int ProgramId, string ProgramName)[] programs,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        SetInstanceSelection(scope, database);

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateDocumentInfo(schoolId, programs),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ReferenceBackedTopLevelCollectionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ReferenceBackedTopLevelCollectionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(upsertRequest);
    }

    /// <summary>Executes a profiled PUT (Update).</summary>
    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        long schoolId,
        JsonNode writeBody,
        DocumentUuid documentUuid,
        BackendProfileWriteContext profileContext,
        (int ProgramId, string ProgramName)[] programs,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        SetInstanceSelection(scope, database);

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateDocumentInfo(schoolId, programs),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new ReferenceBackedTopLevelCollectionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ReferenceBackedTopLevelCollectionAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(updateRequest);
    }

    // ── Read-back helpers ──────────────────────────────────────────────────────

    /// <summary>Reads the DocumentId for a document by UUID.</summary>
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
        return Convert.ToInt64(
            rows[0]["DocumentId"] ?? throw new InvalidOperationException("DocumentId was null"),
            CultureInfo.InvariantCulture
        );
    }

    /// <summary>Reads all SchoolProgram rows for a given School document.</summary>
    public static async Task<IReadOnlyList<SchoolProgramRow>> ReadSchoolProgramsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long schoolDocumentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal",
                   "ProgramReference_DocumentId",
                   "ProgramReference_ProgramId",
                   "ProgramReference_ProgramName"
            FROM "edfi"."SchoolProgram"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", schoolDocumentId)
        );

        return rows.Select(row => new SchoolProgramRow(
                CollectionItemId: Convert.ToInt64(
                    row["CollectionItemId"] ?? throw new InvalidOperationException("CollectionItemId null"),
                    CultureInfo.InvariantCulture
                ),
                SchoolDocumentId: Convert.ToInt64(
                    row["School_DocumentId"] ?? throw new InvalidOperationException("School_DocumentId null"),
                    CultureInfo.InvariantCulture
                ),
                Ordinal: Convert.ToInt32(
                    row["Ordinal"] ?? throw new InvalidOperationException("Ordinal null"),
                    CultureInfo.InvariantCulture
                ),
                ProgramReferenceDocumentId: row["ProgramReference_DocumentId"] is null or DBNull
                    ? null
                    : Convert.ToInt32(row["ProgramReference_DocumentId"], CultureInfo.InvariantCulture),
                ProgramReferenceProgramId: row["ProgramReference_ProgramId"] is null or DBNull
                    ? null
                    : Convert.ToInt32(row["ProgramReference_ProgramId"], CultureInfo.InvariantCulture),
                ProgramReferenceProgramName: row["ProgramReference_ProgramName"] as string
            ))
            .ToArray();
    }

    /// <summary>Returns the count of rows in edfi.SchoolProgram.</summary>
    public static Task<long> ReadSchoolProgramCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM "edfi"."SchoolProgram";
            """
        );

    /// <summary>Returns the count of rows in dms.Document.</summary>
    public static Task<long> ReadDocumentCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM "dms"."Document";
            """
        );
}

// ---------------------------------------------------------------------------
// Test fixture
// ---------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge
{
    private const long SchoolId = 255902;
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("eeee0002-0000-0000-0000-000000000001")
    );

    // Programs used as stable test data
    private static readonly (int ProgramId, string ProgramName) Alpha = (1001, "Alpha Program");
    private static readonly (int ProgramId, string ProgramName) Beta = (1002, "Beta Program");
    private static readonly (int ProgramId, string ProgramName) Gamma = (1003, "Gamma Program");

    // Deterministic UUIDs for program documents (one per program slot, stable across tests)
    private static readonly Guid AlphaProgramUuid = new("aaaa0001-0000-0000-0000-000000000001");
    private static readonly Guid BetaProgramUuid = new("aaaa0001-0000-0000-0000-000000000002");
    private static readonly Guid GammaProgramUuid = new("aaaa0001-0000-0000-0000-000000000003");

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ReferenceBackedTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();

        // Pre-seed all Program documents so dms.ReferentialIdentity rows exist for every
        // program that any test may reference (either in seed or in a profiled PUT body).
        await ReferenceBackedTopLevelCollectionMergeSupport.SeedProgramAsync(
            _database,
            AlphaProgramUuid,
            Alpha.ProgramId,
            Alpha.ProgramName
        );
        await ReferenceBackedTopLevelCollectionMergeSupport.SeedProgramAsync(
            _database,
            BetaProgramUuid,
            Beta.ProgramId,
            Beta.ProgramName
        );
        await ReferenceBackedTopLevelCollectionMergeSupport.SeedProgramAsync(
            _database,
            GammaProgramUuid,
            Gamma.ProgramId,
            Gamma.ProgramName
        );

        _serviceProvider = ReferenceBackedTopLevelCollectionMergeSupport.CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    // ── Test 1: Matched update ────────────────────────────────────────────────

    [Test]
    public async Task It_updates_in_place_when_request_item_matches_stored_visible_row_by_reference_identity()
    {
        // Arrange: seed School with Alpha + Beta + Gamma programs
        var before = await SeedAndReadProgramsAsync(Alpha, Beta, Gamma);
        var idByProgram = before.ToDictionary(
            row => (row.ProgramReferenceProgramId!.Value, row.ProgramReferenceProgramName!),
            row => row.CollectionItemId
        );

        // PUT body contains Beta only (reorder: Beta now at position 1)
        var writeBody = ReferenceBackedTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, Beta);

        // Act
        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new ReferenceBackedRequestItem(Beta.ProgramId, Beta.ProgramName, Creatable: true)],
            visibleStoredRows:
            [
                new ReferenceBackedStoredRow(Alpha.ProgramId, Alpha.ProgramName, []),
                new ReferenceBackedStoredRow(Beta.ProgramId, Beta.ProgramName, []),
            ],
            "ref-backed-update-in-place-put"
        );

        // Assert
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await ReferenceBackedTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await ReferenceBackedTopLevelCollectionMergeSupport.ReadSchoolProgramsAsync(
            _database,
            documentId
        );

        // Beta matched → its CollectionItemId is preserved.
        // Alpha was in visible stored but absent from request → deleted.
        // Gamma was hidden (not in visible stored) → preserved.
        after.Should().HaveCount(2, "Beta updated in-place + Gamma hidden-preserved");
        var betaRow = after.Single(r => r.ProgramReferenceProgramId == Beta.ProgramId);
        betaRow
            .CollectionItemId.Should()
            .Be(idByProgram[(Beta.ProgramId, Beta.ProgramName)], "Beta matched → same CollectionItemId");
        betaRow.Ordinal.Should().Be(0);

        var gammaRow = after.Single(r => r.ProgramReferenceProgramId == Gamma.ProgramId);
        gammaRow
            .CollectionItemId.Should()
            .Be(
                idByProgram[(Gamma.ProgramId, Gamma.ProgramName)],
                "Gamma hidden → CollectionItemId preserved"
            );
    }

    // ── Test 2: Delete-by-absence ─────────────────────────────────────────────

    [Test]
    public async Task It_deletes_visible_row_absent_from_request_while_preserving_hidden_rows()
    {
        // Arrange: seed School with Alpha + Beta + Gamma
        var before = await SeedAndReadProgramsAsync(Alpha, Beta, Gamma);
        var idByProgram = before.ToDictionary(
            row => (row.ProgramReferenceProgramId!.Value, row.ProgramReferenceProgramName!),
            row => row.CollectionItemId
        );

        // PUT body omits Beta (Beta was visible, so it should be deleted)
        var writeBody = ReferenceBackedTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, Alpha);

        // Act
        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems:
            [
                new ReferenceBackedRequestItem(Alpha.ProgramId, Alpha.ProgramName, Creatable: true),
            ],
            visibleStoredRows:
            [
                new ReferenceBackedStoredRow(Alpha.ProgramId, Alpha.ProgramName, []),
                new ReferenceBackedStoredRow(Beta.ProgramId, Beta.ProgramName, []),
            ],
            "ref-backed-delete-by-absence-put"
        );

        // Assert
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await ReferenceBackedTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await ReferenceBackedTopLevelCollectionMergeSupport.ReadSchoolProgramsAsync(
            _database,
            documentId
        );

        // Alpha matched (present in request) → stays.
        // Beta was visible but absent from request → deleted.
        // Gamma was hidden → preserved.
        after.Should().HaveCount(2, "Alpha kept + Gamma hidden-preserved; Beta deleted");
        after.Should().Contain(r => r.ProgramReferenceProgramId == Alpha.ProgramId, "Alpha preserved");
        after
            .Should()
            .NotContain(r => r.ProgramReferenceProgramId == Beta.ProgramId, "Beta deleted by absence");
        after.Should().Contain(r => r.ProgramReferenceProgramId == Gamma.ProgramId, "Gamma hidden-preserved");

        var alphaRow = after.Single(r => r.ProgramReferenceProgramId == Alpha.ProgramId);
        alphaRow
            .CollectionItemId.Should()
            .Be(idByProgram[(Alpha.ProgramId, Alpha.ProgramName)], "Alpha matched → CollectionItemId stable");
        var gammaRow = after.Single(r => r.ProgramReferenceProgramId == Gamma.ProgramId);
        gammaRow
            .CollectionItemId.Should()
            .Be(idByProgram[(Gamma.ProgramId, Gamma.ProgramName)], "Gamma hidden → CollectionItemId stable");
    }

    // ── Test 3: Insert-when-creatable ─────────────────────────────────────────

    [Test]
    public async Task It_inserts_new_visible_item_when_creatable_and_no_prior_match_exists()
    {
        // Arrange: seed School with Alpha only
        var before = await SeedAndReadProgramsAsync(Alpha);
        var alphaId = before.Single().CollectionItemId;

        // PUT body adds Gamma (new, not previously visible)
        var writeBody = ReferenceBackedTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            Alpha,
            Gamma
        );

        // Act
        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems:
            [
                new ReferenceBackedRequestItem(Alpha.ProgramId, Alpha.ProgramName, Creatable: true),
                new ReferenceBackedRequestItem(Gamma.ProgramId, Gamma.ProgramName, Creatable: true),
            ],
            visibleStoredRows: [new ReferenceBackedStoredRow(Alpha.ProgramId, Alpha.ProgramName, [])],
            "ref-backed-insert-creatable-put"
        );

        // Assert
        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await ReferenceBackedTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await ReferenceBackedTopLevelCollectionMergeSupport.ReadSchoolProgramsAsync(
            _database,
            documentId
        );

        after.Should().HaveCount(2, "Alpha updated in-place + Gamma newly inserted");
        var alphaRow = after.Single(r => r.ProgramReferenceProgramId == Alpha.ProgramId);
        alphaRow.CollectionItemId.Should().Be(alphaId, "Alpha matched → same CollectionItemId");
        alphaRow.Ordinal.Should().Be(0);

        var gammaRow = after.Single(r => r.ProgramReferenceProgramId == Gamma.ProgramId);
        gammaRow.Ordinal.Should().Be(1);
        gammaRow.CollectionItemId.Should().BeGreaterThan(0);
        gammaRow.CollectionItemId.Should().NotBe(alphaId, "Gamma is a new row");
        gammaRow.ProgramReferenceProgramName.Should().Be(Gamma.ProgramName);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SchoolProgramRow>> SeedAndReadProgramsAsync(
        params (int ProgramId, string ProgramName)[] programs
    )
    {
        // Note: Program documents are pre-seeded in SetUp for all three stable programs
        // (Alpha, Beta, Gamma) so dms.ReferentialIdentity exists before this method is called.

        var seedBody = ReferenceBackedTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, programs);
        var seedResult = await ReferenceBackedTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            seedBody,
            SchoolDocumentUuid,
            programs,
            "ref-backed-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>("seed should insert");

        var documentId = await ReferenceBackedTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        return await ReferenceBackedTopLevelCollectionMergeSupport.ReadSchoolProgramsAsync(
            _database,
            documentId
        );
    }

    private async Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        IReadOnlyList<ReferenceBackedRequestItem> requestItems,
        IReadOnlyList<ReferenceBackedStoredRow> visibleStoredRows,
        string traceLabel
    )
    {
        var writePlan = _mappingSet.WritePlansByResource[
            ReferenceBackedTopLevelCollectionMergeSupport.SchoolResource
        ];
        var profileContext = ReferenceBackedTopLevelCollectionMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            requestItems,
            visibleStoredRows
        );

        // Pass the request items as program references so the resolver can find each referenced Program.
        var programs = requestItems.Select(item => (item.ProgramId, item.ProgramName)).ToArray();

        return await ReferenceBackedTopLevelCollectionMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            writeBody,
            SchoolDocumentUuid,
            profileContext,
            programs,
            traceLabel
        );
    }
}
