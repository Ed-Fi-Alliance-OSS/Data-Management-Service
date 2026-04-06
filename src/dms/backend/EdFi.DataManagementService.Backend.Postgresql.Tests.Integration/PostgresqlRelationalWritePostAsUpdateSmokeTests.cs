// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class PostAsUpdateNoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostAsUpdateAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostAsUpdateNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
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

internal sealed class ConcurrentPostCreateRaceCoordinator
{
    private readonly TaskCompletionSource _firstResolverCallPending = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly TaskCompletionSource _allowFirstResolverCall = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private int _resolveForPostCallCount;

    public async Task WaitForFirstResolverCallWindowAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _resolveForPostCallCount) != 1)
        {
            return;
        }

        _firstResolverCallPending.TrySetResult();
        await _allowFirstResolverCall.Task.WaitAsync(cancellationToken);
    }

    public Task WaitUntilFirstResolverCallIsPendingAsync(CancellationToken cancellationToken = default) =>
        _firstResolverCallPending.Task.WaitAsync(cancellationToken);

    public void ReleaseFirstResolverCall() => _allowFirstResolverCall.TrySetResult();
}

internal sealed class BlockingPostTargetLookupResolver(ConcurrentPostCreateRaceCoordinator coordinator)
    : IRelationalWriteTargetLookupResolver
{
    private readonly ConcurrentPostCreateRaceCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    private readonly RelationalWriteTargetLookupResolver _innerResolver = new();

    public async Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    )
    {
        await _coordinator.WaitForFirstResolverCallWindowAsync(cancellationToken);

        return await _innerResolver.ResolveForPostAsync(
            mappingSet,
            resource,
            referentialId,
            candidateDocumentUuid,
            connection,
            transaction,
            cancellationToken
        );
    }
}

file static class PostAsUpdateIntegrationTestSupport
{
    public static ServiceProvider CreateServiceProvider(
        ConcurrentPostCreateRaceCoordinator? raceCoordinator = null
    )
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, PostAsUpdateNoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddScoped<RelationalDocumentStoreRepository>();

        if (raceCoordinator is not null)
        {
            services.AddSingleton(raceCoordinator);
            services.AddScoped<IRelationalWriteTargetLookupResolver, BlockingPostTargetLookupResolver>();
        }

        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static decimal GetDecimal(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToDecimal(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    public static bool GetBoolean(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is bool value
            ? value
            : throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a boolean value."
            );

    public static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    public static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value as string
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException(
                $"Expected persisted row to contain non-null column '{columnName}'."
            );
        }

        return value;
    }
}

internal sealed record FocusedPostAsUpdateDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record FocusedPostAsUpdateSchoolRow(long DocumentId, long SchoolId, string? ShortName);

internal sealed record FocusedPostAsUpdateSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record FocusedPostAsUpdateSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Post_As_Update_Immutable_Identity_Change_With_A_Focused_Stable_Key_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const string CreateRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Austin"
            },
            {
              "city": "Dallas"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    private const string ImmutableIdentityPostAsUpdateRequestBodyJson = """
        {
          "schoolId": 255902,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Austin"
            },
            {
              "city": "Dallas"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000005")
    );
    private static readonly DocumentUuid RejectedPostAsUpdateDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000006")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private FocusedPostAsUpdateDocumentRow _documentBeforeRejectedPostAsUpdate = null!;
    private FocusedPostAsUpdateDocumentRow _documentAfterRejectedPostAsUpdate = null!;
    private FocusedPostAsUpdateSchoolRow _schoolBeforeRejectedPostAsUpdate = null!;
    private FocusedPostAsUpdateSchoolRow _schoolAfterRejectedPostAsUpdate = null!;
    private UpsertResult _rejectedPostAsUpdateResult = null!;
    private ReferentialId _persistedSchoolReferentialId;
    private long _documentCount;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostAsUpdateIntegrationTestSupport.CreateServiceProvider();

        var createResult = await ExecuteUpsertAsync(
            CreateRequestBodyJson,
            ExistingSchoolDocumentUuid,
            "pg-post-as-update-immutable-identity-create"
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        _documentBeforeRejectedPostAsUpdate = await ReadDocumentAsync(ExistingSchoolDocumentUuid.Value);
        _schoolBeforeRejectedPostAsUpdate = await ReadSchoolAsync(
            _documentBeforeRejectedPostAsUpdate.DocumentId
        );
        _persistedSchoolReferentialId = new ReferentialId(
            (
                await ReadReferentialIdentityRowAsync(
                    _documentBeforeRejectedPostAsUpdate.DocumentId,
                    _mappingSet.ResourceKeyIdByResource[SchoolResource]
                )
            ).ReferentialId
        );

        _rejectedPostAsUpdateResult = await ExecuteUpsertAsync(
            ImmutableIdentityPostAsUpdateRequestBodyJson,
            RejectedPostAsUpdateDocumentUuid,
            "pg-post-as-update-immutable-identity-reject",
            schoolId: 255902,
            referentialId: _persistedSchoolReferentialId
        );

        _documentAfterRejectedPostAsUpdate = await ReadDocumentAsync(ExistingSchoolDocumentUuid.Value);
        _schoolAfterRejectedPostAsUpdate = await ReadSchoolAsync(
            _documentAfterRejectedPostAsUpdate.DocumentId
        );
        _documentCount = await ReadDocumentCountAsync();
        _incomingDocumentUuidCount = await ReadDocumentCountAsync(RejectedPostAsUpdateDocumentUuid.Value);
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_returns_explicit_immutable_identity_failure_for_post_as_update()
    {
        _rejectedPostAsUpdateResult.Should().BeOfType<UpsertResult.UpsertFailureImmutableIdentity>();
        _rejectedPostAsUpdateResult.Should().NotBeOfType<UpsertResult.UnknownFailure>();
        _rejectedPostAsUpdateResult
            .As<UpsertResult.UpsertFailureImmutableIdentity>()
            .FailureMessage.Should()
            .Be(
                "Identifying values for the School resource cannot be changed. Delete and recreate the resource item instead."
            );
    }

    [Test]
    public void It_does_not_commit_row_changes_for_rejected_post_as_update()
    {
        _documentAfterRejectedPostAsUpdate.Should().Be(_documentBeforeRejectedPostAsUpdate);
        _schoolAfterRejectedPostAsUpdate.Should().Be(_schoolBeforeRejectedPostAsUpdate);
        _documentCount.Should().Be(1);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        long schoolId = 255901,
        ReferentialId? referentialId = null
    )
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWritePostAsUpdateImmutableIdentity",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            CreateUpsertRequest(requestBodyJson, documentUuid, traceId, schoolId, referentialId)
        );
    }

    private UpsertRequest CreateUpsertRequest(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        long schoolId,
        ReferentialId? referentialId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(schoolId, referentialId),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    private static DocumentInfo CreateSchoolDocumentInfo(long schoolId, ReferentialId? referentialId = null)
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                schoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private async Task<FocusedPostAsUpdateDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new FocusedPostAsUpdateDocumentRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<FocusedPostAsUpdateSchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId", "ShortName"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new FocusedPostAsUpdateSchoolRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "SchoolId"),
                PostAsUpdateIntegrationTestSupport.GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<ReferentialIdentityRow> ReadReferentialIdentityRowAsync(
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
                AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new ReferentialIdentityRow(
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "ReferentialId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }

    private async Task<long> ReadDocumentCountAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document";
            """
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<long> ReadDocumentCountAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const string FallTermDescriptorUri = "uri://ed-fi.org/TermDescriptor#Fall";
    private const string HonorRollAcademicHonorCategoryDescriptorUri =
        "uri://ed-fi.org/AcademicHonorCategoryDescriptor#HonorRoll";
    private const string ScholarAthleteAcademicHonorCategoryDescriptorUri =
        "uri://ed-fi.org/AcademicHonorCategoryDescriptor#ScholarAthlete";
    private const string CommunityServiceAcademicHonorCategoryDescriptorUri =
        "uri://ed-fi.org/AcademicHonorCategoryDescriptor#CommunityService";
    private const string StandardDiplomaTypeDescriptorUri =
        "uri://ed-fi.org/DiplomaTypeDescriptor#StandardDiploma";
    private const string CareerDiplomaTypeDescriptorUri =
        "uri://ed-fi.org/DiplomaTypeDescriptor#CareerDiploma";
    private const string HonorsDiplomaTypeDescriptorUri =
        "uri://ed-fi.org/DiplomaTypeDescriptor#HonorsDiploma";
    private const string CumulativeGradePointAverageTypeDescriptorUri =
        "uri://ed-fi.org/GradePointAverageTypeDescriptor#Cumulative";
    private const string SessionGradePointAverageTypeDescriptorUri =
        "uri://ed-fi.org/GradePointAverageTypeDescriptor#Session";
    private const string WeightedGradePointAverageTypeDescriptorUri =
        "uri://ed-fi.org/GradePointAverageTypeDescriptor#Weighted";
    private const string MeritRecognitionTypeDescriptorUri =
        "uri://ed-fi.org/RecognitionTypeDescriptor#Merit";
    private const string LeadershipRecognitionTypeDescriptorUri =
        "uri://ed-fi.org/RecognitionTypeDescriptor#Leadership";
    private const string AttendanceRecognitionTypeDescriptorUri =
        "uri://ed-fi.org/RecognitionTypeDescriptor#Attendance";

    private static readonly QualifiedResourceName StudentAcademicRecordResource = new(
        "Ed-Fi",
        "StudentAcademicRecord"
    );
    private static readonly DocumentUuid StudentAcademicRecordDocumentUuid = new(
        Guid.Parse("dddddddd-1111-1111-1111-111111111111")
    );
    private static readonly IReadOnlyList<AcademicHonorSpec> CreateAcademicHonorSpecs =
    [
        .. Enumerable.Range(1, 12).Select(index => CreateAcademicHonorSpec(index, isUpdated: false)),
    ];
    private static readonly IReadOnlyList<AcademicHonorSpec> ChangedAcademicHonorSpecs =
    [
        .. ((IReadOnlyList<int>)[8, 3, 11, 1, 12, 5, 13, 9, 2, 14, 10, 4]).Select(index =>
            CreateAcademicHonorSpec(index, isUpdated: true)
        ),
    ];
    private static readonly IReadOnlyList<DiplomaSpec> CreateDiplomaSpecs =
    [
        .. Enumerable.Range(1, 12).Select(index => CreateDiplomaSpec(index, isUpdated: false)),
    ];
    private static readonly IReadOnlyList<DiplomaSpec> ChangedDiplomaSpecs =
    [
        .. ((IReadOnlyList<int>)[6, 2, 10, 1, 12, 4, 13, 8, 3, 14, 11, 5]).Select(index =>
            CreateDiplomaSpec(index, isUpdated: true)
        ),
    ];
    private static readonly IReadOnlyList<GradePointAverageSpec> CreateGradePointAverageSpecs =
    [
        new(CumulativeGradePointAverageTypeDescriptorUri, 3.4500m, true, 4.0000m),
        new(SessionGradePointAverageTypeDescriptorUri, 3.7200m, false, 4.0000m),
    ];
    private static readonly IReadOnlyList<GradePointAverageSpec> ChangedGradePointAverageSpecs =
    [
        new(WeightedGradePointAverageTypeDescriptorUri, 4.1800m, false, 5.0000m),
        new(CumulativeGradePointAverageTypeDescriptorUri, 3.6100m, true, 4.0000m),
    ];
    private static readonly IReadOnlyList<RecognitionSpec> CreateRecognitionSpecs =
    [
        new(MeritRecognitionTypeDescriptorUri, "Regional Merit", "State Board"),
        new(LeadershipRecognitionTypeDescriptorUri, "Leadership Award", "State Board"),
    ];
    private static readonly IReadOnlyList<RecognitionSpec> ChangedRecognitionSpecs =
    [
        new(AttendanceRecognitionTypeDescriptorUri, "Perfect Attendance", "District Office"),
        new(MeritRecognitionTypeDescriptorUri, "State Merit", "State Board"),
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private ResourceSchema _extensionResourceSchema = null!;
    private AuthoritativeStudentAcademicRecordSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeStudentAcademicRecordPersistedState _stateAfterCreate = null!;
    private AuthoritativeStudentAcademicRecordPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeStudentAcademicRecordPersistedState _stateAfterNoOpUpdate = null!;
    private int _createCollectionRowCount;
    private int _createCollectionInsertParameterCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostAsUpdateIntegrationTestSupport.CreateServiceProvider();

        var (baseProjectSchema, baseResourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "StudentAcademicRecord"
        );
        var (_, extensionResourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "sample",
            "StudentAcademicRecord"
        );

        _resourceInfo = CreateResourceInfo(baseProjectSchema, baseResourceSchema);
        _baseResourceSchema = baseResourceSchema;
        _extensionResourceSchema = extensionResourceSchema;
        _seedData = await SeedReferenceDataAsync();
        _createCollectionRowCount =
            CreateAcademicHonorSpecs.Count
            + CreateDiplomaSpecs.Count
            + CreateGradePointAverageSpecs.Count
            + CreateRecognitionSpecs.Count;
        _createCollectionInsertParameterCount = CalculateCreateCollectionInsertParameterCount();

        _createResult = await ExecuteCreateAsync();

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail($"Create reference failure: {FormatReferenceFailure(createReferenceFailure)}");
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(StudentAcademicRecordDocumentUuid.Value);

        _changedUpdateResult = await ExecuteChangedUpdateAsync();
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(StudentAcademicRecordDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteNoOpUpdateAsync();
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(StudentAcademicRecordDocumentUuid.Value);
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_persists_authoritative_student_academic_record_root_extension_and_large_collection_rows_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(StudentAcademicRecordDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[StudentAcademicRecordResource]);
        _stateAfterCreate
            .AcademicRecord.Should()
            .Be(
                new AuthoritativeStudentAcademicRecordRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.SchoolDocumentId,
                    100,
                    _seedData.SchoolYearTypeDocumentId,
                    2026,
                    _seedData.StudentDocumentId,
                    "10001",
                    _seedData.FallTermDescriptorDocumentId,
                    18.500m,
                    "2028-05-20"
                )
            );
        _stateAfterCreate
            .AcademicRecordExtension.Should()
            .Be(
                new AuthoritativeStudentAcademicRecordExtensionRow(
                    _stateAfterCreate.Document.DocumentId,
                    "Initial transcript note"
                )
            );

        var createAcademicHonorIdsByDescription = CreateAcademicHonorIdsByDescription(
            _stateAfterCreate.AcademicHonors
        );
        var createDiplomaIdsByAwardDate = CreateDiplomaIdsByAwardDate(_stateAfterCreate.Diplomas);
        var createGradePointAverageIdsByKey = CreateGradePointAverageIdsByKey(
            _stateAfterCreate.GradePointAverages
        );
        var createRecognitionIdsByKey = CreateRecognitionIdsByKey(_stateAfterCreate.Recognitions);

        _stateAfterCreate
            .AcademicHonors.Should()
            .Equal(
                CreateExpectedAcademicHonors(
                    CreateAcademicHonorSpecs,
                    createAcademicHonorIdsByDescription,
                    _stateAfterCreate.Document.DocumentId
                )
            );
        _stateAfterCreate
            .Diplomas.Should()
            .Equal(
                CreateExpectedDiplomas(
                    CreateDiplomaSpecs,
                    createDiplomaIdsByAwardDate,
                    _stateAfterCreate.Document.DocumentId
                )
            );
        _stateAfterCreate
            .GradePointAverages.Should()
            .Equal(
                CreateExpectedGradePointAverages(
                    CreateGradePointAverageSpecs,
                    createGradePointAverageIdsByKey,
                    _stateAfterCreate.Document.DocumentId
                )
            );
        _stateAfterCreate
            .Recognitions.Should()
            .Equal(
                CreateExpectedRecognitions(
                    CreateRecognitionSpecs,
                    createRecognitionIdsByKey,
                    _stateAfterCreate.Document.DocumentId
                )
            );
    }

    [Test]
    public void It_uses_a_payload_large_enough_to_exercise_real_parameter_pressure()
    {
        _createCollectionRowCount.Should().Be(28);
        _createCollectionInsertParameterCount.Should().BeGreaterThan(300);
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_across_large_collection_tables_for_a_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(StudentAcademicRecordDocumentUuid);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
        _stateAfterChangedUpdate.Document.DocumentUuid.Should().Be(StudentAcademicRecordDocumentUuid.Value);
        _stateAfterChangedUpdate.Document.DocumentId.Should().Be(_stateAfterCreate.Document.DocumentId);
        _stateAfterChangedUpdate
            .AcademicRecord.Should()
            .Be(
                new AuthoritativeStudentAcademicRecordRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.SchoolDocumentId,
                    100,
                    _seedData.SchoolYearTypeDocumentId,
                    2026,
                    _seedData.StudentDocumentId,
                    "10001",
                    _seedData.FallTermDescriptorDocumentId,
                    21.750m,
                    "2028-05-24"
                )
            );
        _stateAfterChangedUpdate
            .AcademicRecordExtension.Should()
            .Be(
                new AuthoritativeStudentAcademicRecordExtensionRow(
                    _stateAfterCreate.Document.DocumentId,
                    "Updated transcript note"
                )
            );

        var createAcademicHonorIdsByDescription = CreateAcademicHonorIdsByDescription(
            _stateAfterCreate.AcademicHonors
        );
        var changedAcademicHonorIdsByDescription = CreateAcademicHonorIdsByDescription(
            _stateAfterChangedUpdate.AcademicHonors
        );
        var expectedAcademicHonorIdsByDescription = CreateExpectedIdsByKey(
            ChangedAcademicHonorSpecs.Select(spec => spec.HonorDescription),
            createAcademicHonorIdsByDescription,
            changedAcademicHonorIdsByDescription
        );

        _stateAfterChangedUpdate
            .AcademicHonors.Should()
            .Equal(
                CreateExpectedAcademicHonors(
                    ChangedAcademicHonorSpecs,
                    expectedAcademicHonorIdsByDescription,
                    _stateAfterCreate.Document.DocumentId
                )
            );

        foreach (
            var deletedHonorDescription in CreateAcademicHonorSpecs
                .Select(spec => spec.HonorDescription)
                .Except(
                    ChangedAcademicHonorSpecs.Select(spec => spec.HonorDescription),
                    StringComparer.Ordinal
                )
        )
        {
            changedAcademicHonorIdsByDescription.Keys.Should().NotContain(deletedHonorDescription);
            changedAcademicHonorIdsByDescription
                .Values.Should()
                .NotContain(createAcademicHonorIdsByDescription[deletedHonorDescription]);
        }

        foreach (
            var insertedHonorDescription in ChangedAcademicHonorSpecs
                .Select(spec => spec.HonorDescription)
                .Except(
                    CreateAcademicHonorSpecs.Select(spec => spec.HonorDescription),
                    StringComparer.Ordinal
                )
        )
        {
            createAcademicHonorIdsByDescription
                .Values.Should()
                .NotContain(changedAcademicHonorIdsByDescription[insertedHonorDescription]);
        }

        var createDiplomaIdsByAwardDate = CreateDiplomaIdsByAwardDate(_stateAfterCreate.Diplomas);
        var changedDiplomaIdsByAwardDate = CreateDiplomaIdsByAwardDate(_stateAfterChangedUpdate.Diplomas);
        var expectedDiplomaIdsByAwardDate = CreateExpectedIdsByKey(
            ChangedDiplomaSpecs.Select(spec => spec.DiplomaAwardDate),
            createDiplomaIdsByAwardDate,
            changedDiplomaIdsByAwardDate
        );

        _stateAfterChangedUpdate
            .Diplomas.Should()
            .Equal(
                CreateExpectedDiplomas(
                    ChangedDiplomaSpecs,
                    expectedDiplomaIdsByAwardDate,
                    _stateAfterCreate.Document.DocumentId
                )
            );

        foreach (
            var deletedDiplomaAwardDate in CreateDiplomaSpecs
                .Select(spec => spec.DiplomaAwardDate)
                .Except(ChangedDiplomaSpecs.Select(spec => spec.DiplomaAwardDate), StringComparer.Ordinal)
        )
        {
            changedDiplomaIdsByAwardDate.Keys.Should().NotContain(deletedDiplomaAwardDate);
            changedDiplomaIdsByAwardDate
                .Values.Should()
                .NotContain(createDiplomaIdsByAwardDate[deletedDiplomaAwardDate]);
        }

        foreach (
            var insertedDiplomaAwardDate in ChangedDiplomaSpecs
                .Select(spec => spec.DiplomaAwardDate)
                .Except(CreateDiplomaSpecs.Select(spec => spec.DiplomaAwardDate), StringComparer.Ordinal)
        )
        {
            createDiplomaIdsByAwardDate
                .Values.Should()
                .NotContain(changedDiplomaIdsByAwardDate[insertedDiplomaAwardDate]);
        }

        var createGradePointAverageIdsByKey = CreateGradePointAverageIdsByKey(
            _stateAfterCreate.GradePointAverages
        );
        var changedGradePointAverageIdsByKey = CreateGradePointAverageIdsByKey(
            _stateAfterChangedUpdate.GradePointAverages
        );
        var expectedGradePointAverageIdsByKey = CreateExpectedIdsByKey(
            ChangedGradePointAverageSpecs.Select(spec => ResolveGradePointAverageKey(spec.DescriptorUri)),
            createGradePointAverageIdsByKey,
            changedGradePointAverageIdsByKey
        );

        _stateAfterChangedUpdate
            .GradePointAverages.Should()
            .Equal(
                CreateExpectedGradePointAverages(
                    ChangedGradePointAverageSpecs,
                    expectedGradePointAverageIdsByKey,
                    _stateAfterCreate.Document.DocumentId
                )
            );

        changedGradePointAverageIdsByKey.Keys.Should().NotContain("Session");
        changedGradePointAverageIdsByKey
            .Values.Should()
            .NotContain(createGradePointAverageIdsByKey["Session"]);
        createGradePointAverageIdsByKey
            .Values.Should()
            .NotContain(changedGradePointAverageIdsByKey["Weighted"]);

        var createRecognitionIdsByKey = CreateRecognitionIdsByKey(_stateAfterCreate.Recognitions);
        var changedRecognitionIdsByKey = CreateRecognitionIdsByKey(_stateAfterChangedUpdate.Recognitions);
        var expectedRecognitionIdsByKey = CreateExpectedIdsByKey(
            ChangedRecognitionSpecs.Select(spec => ResolveRecognitionKey(spec.DescriptorUri)),
            createRecognitionIdsByKey,
            changedRecognitionIdsByKey
        );

        _stateAfterChangedUpdate
            .Recognitions.Should()
            .Equal(
                CreateExpectedRecognitions(
                    ChangedRecognitionSpecs,
                    expectedRecognitionIdsByKey,
                    _stateAfterCreate.Document.DocumentId
                )
            );

        changedRecognitionIdsByKey.Keys.Should().NotContain("Leadership");
        changedRecognitionIdsByKey.Values.Should().NotContain(createRecognitionIdsByKey["Leadership"]);
        createRecognitionIdsByKey.Values.Should().NotContain(changedRecognitionIdsByKey["Attendance"]);
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put()
    {
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _noOpUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(StudentAcademicRecordDocumentUuid);
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateAfterChangedUpdate);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentAcademicRecord",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = CreateRequestBody(
            cumulativeEarnedCredits: 18.500m,
            projectedGraduationDate: "2028-05-20",
            notes: "Initial transcript note",
            academicHonors: CreateAcademicHonorSpecs,
            diplomas: CreateDiplomaSpecs,
            gradePointAverages: CreateGradePointAverageSpecs,
            recognitions: CreateRecognitionSpecs
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(
                new UpsertRequest(
                    ResourceInfo: _resourceInfo,
                    DocumentInfo: CreateDocumentInfo(requestBody),
                    MappingSet: _mappingSet,
                    EdfiDoc: requestBody,
                    Headers: [],
                    TraceId: new TraceId("pg-authoritative-sample-student-academic-record-create"),
                    DocumentUuid: StudentAcademicRecordDocumentUuid,
                    DocumentSecurityElements: new([], [], [], [], []),
                    UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
                    ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
                    ResourceAuthorizationPathways: []
                )
            );
    }

    private async Task<UpdateResult> ExecuteChangedUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentAcademicRecord",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = CreateRequestBody(
            cumulativeEarnedCredits: 21.750m,
            projectedGraduationDate: "2028-05-24",
            notes: "Updated transcript note",
            academicHonors: ChangedAcademicHonorSpecs,
            diplomas: ChangedDiplomaSpecs,
            gradePointAverages: ChangedGradePointAverageSpecs,
            recognitions: ChangedRecognitionSpecs
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(
                new UpdateRequest(
                    ResourceInfo: _resourceInfo,
                    DocumentInfo: CreateDocumentInfo(requestBody),
                    MappingSet: _mappingSet,
                    EdfiDoc: requestBody,
                    Headers: [],
                    TraceId: new TraceId("pg-authoritative-sample-student-academic-record-changed-update"),
                    DocumentUuid: StudentAcademicRecordDocumentUuid,
                    DocumentSecurityElements: new([], [], [], [], []),
                    UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
                    ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
                    ResourceAuthorizationPathways: []
                )
            );
    }

    private async Task<UpdateResult> ExecuteNoOpUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentAcademicRecord",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = CreateRequestBody(
            cumulativeEarnedCredits: 21.750m,
            projectedGraduationDate: "2028-05-24",
            notes: "Updated transcript note",
            academicHonors: ChangedAcademicHonorSpecs,
            diplomas: ChangedDiplomaSpecs,
            gradePointAverages: ChangedGradePointAverageSpecs,
            recognitions: ChangedRecognitionSpecs
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(
                new UpdateRequest(
                    ResourceInfo: _resourceInfo,
                    DocumentInfo: CreateDocumentInfo(requestBody),
                    MappingSet: _mappingSet,
                    EdfiDoc: requestBody,
                    Headers: [],
                    TraceId: new TraceId("pg-authoritative-sample-student-academic-record-no-op-update"),
                    DocumentUuid: StudentAcademicRecordDocumentUuid,
                    DocumentSecurityElements: new([], [], [], [], []),
                    UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
                    ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
                    ResourceAuthorizationPathways: []
                )
            );
    }

    private JsonNode CreateRequestBody(
        decimal cumulativeEarnedCredits,
        string projectedGraduationDate,
        string notes,
        IReadOnlyList<AcademicHonorSpec> academicHonors,
        IReadOnlyList<DiplomaSpec> diplomas,
        IReadOnlyList<GradePointAverageSpec> gradePointAverages,
        IReadOnlyList<RecognitionSpec> recognitions
    ) =>
        JsonSerializer.SerializeToNode(
            new
            {
                educationOrganizationReference = new { educationOrganizationId = 100 },
                schoolYearTypeReference = new { schoolYear = 2026 },
                studentReference = new { studentUniqueId = "10001" },
                termDescriptor = FallTermDescriptorUri,
                cumulativeEarnedCredits,
                projectedGraduationDate,
                academicHonors = academicHonors
                    .Select(spec => new
                    {
                        academicHonorCategoryDescriptor = spec.DescriptorUri,
                        honorDescription = spec.HonorDescription,
                        issuerName = spec.IssuerName,
                    })
                    .ToArray(),
                diplomas = diplomas
                    .Select(spec => new
                    {
                        diplomaTypeDescriptor = spec.DescriptorUri,
                        diplomaAwardDate = spec.DiplomaAwardDate,
                        diplomaDescription = spec.DiplomaDescription,
                    })
                    .ToArray(),
                gradePointAverages = gradePointAverages
                    .Select(spec => new
                    {
                        gradePointAverageTypeDescriptor = spec.DescriptorUri,
                        gradePointAverageValue = spec.GradePointAverageValue,
                        isCumulative = spec.IsCumulative,
                        maxGradePointAverageValue = spec.MaxGradePointAverageValue,
                    })
                    .ToArray(),
                recognitions = recognitions
                    .Select(spec => new
                    {
                        recognitionTypeDescriptor = spec.DescriptorUri,
                        recognitionDescription = spec.RecognitionDescription,
                        issuerName = spec.IssuerName,
                    })
                    .ToArray(),
                _ext = new { sample = new { notes } },
            }
        ) ?? throw new InvalidOperationException("Expected StudentAcademicRecord request body to serialize.");

    private DocumentInfo CreateDocumentInfo(JsonNode requestBody)
    {
        var (documentIdentity, superclassIdentity) = _baseResourceSchema.ExtractIdentities(
            requestBody,
            NullLogger.Instance
        );

        var (baseDocumentReferences, baseDocumentReferenceArrays) = _baseResourceSchema.ExtractReferences(
            requestBody,
            NullLogger.Instance
        );

        var (extensionDocumentReferences, extensionDocumentReferenceArrays) =
            _extensionResourceSchema.ExtractReferences(requestBody, NullLogger.Instance);

        var descriptorReferences = _baseResourceSchema
            .ExtractDescriptors(requestBody, NullLogger.Instance)
            .Concat(_extensionResourceSchema.ExtractDescriptors(requestBody, NullLogger.Instance))
            .ToArray();

        return new(
            DocumentIdentity: documentIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(_resourceInfo, documentIdentity),
            DocumentReferences: [.. baseDocumentReferences, .. extensionDocumentReferences],
            DocumentReferenceArrays: [.. baseDocumentReferenceArrays, .. extensionDocumentReferenceArrays],
            DescriptorReferences: descriptorReferences,
            SuperclassIdentity: superclassIdentity
        );
    }

    private static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        var effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(
                project.ProjectEndpointName,
                projectEndpointName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project '{projectEndpointName}'."
            );

        return (projectSchema, new ResourceSchema(resourceSchemaNode));
    }

    private static ResourceInfo CreateResourceInfo(
        ProjectSchema projectSchema,
        ResourceSchema resourceSchema
    ) =>
        new(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

    private static string FormatReferenceFailure(UpsertResult.UpsertFailureReference failure)
    {
        var documentFailures = failure.InvalidDocumentReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );
        var descriptorFailures = failure.InvalidDescriptorReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );

        return string.Join(" | ", documentFailures.Concat(descriptorFailures));
    }

    private async Task<AuthoritativeStudentAcademicRecordSeedData> SeedReferenceDataAsync()
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");
        var academicHonorCategoryDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "AcademicHonorCategoryDescriptor"
        );
        var diplomaTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "DiplomaTypeDescriptor"
        );
        var gradePointAverageTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradePointAverageTypeDescriptor"
        );
        var recognitionTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "RecognitionTypeDescriptor"
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, 100, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "School", false), ("$.schoolId", "100")),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", "100")
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, 2026, true, "2025-2026");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "SchoolYearType", false), ("$.schoolYear", "2026")),
            schoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, "10001", "Casey", "Cole");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", "10001")),
            studentDocumentId,
            studentResourceKeyId
        );

        var fallTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            FallTermDescriptorUri,
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "TermDescriptor", FallTermDescriptorUri),
            fallTermDescriptorDocumentId,
            termDescriptorResourceKeyId
        );

        var honorRollAcademicHonorCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            academicHonorCategoryDescriptorResourceKeyId,
            "Ed-Fi:AcademicHonorCategoryDescriptor",
            HonorRollAcademicHonorCategoryDescriptorUri,
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor",
            "HonorRoll",
            "Honor Roll"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AcademicHonorCategoryDescriptor",
                HonorRollAcademicHonorCategoryDescriptorUri
            ),
            honorRollAcademicHonorCategoryDescriptorDocumentId,
            academicHonorCategoryDescriptorResourceKeyId
        );

        var scholarAthleteAcademicHonorCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            academicHonorCategoryDescriptorResourceKeyId,
            "Ed-Fi:AcademicHonorCategoryDescriptor",
            ScholarAthleteAcademicHonorCategoryDescriptorUri,
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor",
            "ScholarAthlete",
            "Scholar Athlete"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AcademicHonorCategoryDescriptor",
                ScholarAthleteAcademicHonorCategoryDescriptorUri
            ),
            scholarAthleteAcademicHonorCategoryDescriptorDocumentId,
            academicHonorCategoryDescriptorResourceKeyId
        );

        var communityServiceAcademicHonorCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            academicHonorCategoryDescriptorResourceKeyId,
            "Ed-Fi:AcademicHonorCategoryDescriptor",
            CommunityServiceAcademicHonorCategoryDescriptorUri,
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor",
            "CommunityService",
            "Community Service"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AcademicHonorCategoryDescriptor",
                CommunityServiceAcademicHonorCategoryDescriptorUri
            ),
            communityServiceAcademicHonorCategoryDescriptorDocumentId,
            academicHonorCategoryDescriptorResourceKeyId
        );

        var standardDiplomaTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            diplomaTypeDescriptorResourceKeyId,
            "Ed-Fi:DiplomaTypeDescriptor",
            StandardDiplomaTypeDescriptorUri,
            "uri://ed-fi.org/DiplomaTypeDescriptor",
            "StandardDiploma",
            "Standard Diploma"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "DiplomaTypeDescriptor", StandardDiplomaTypeDescriptorUri),
            standardDiplomaTypeDescriptorDocumentId,
            diplomaTypeDescriptorResourceKeyId
        );

        var careerDiplomaTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            diplomaTypeDescriptorResourceKeyId,
            "Ed-Fi:DiplomaTypeDescriptor",
            CareerDiplomaTypeDescriptorUri,
            "uri://ed-fi.org/DiplomaTypeDescriptor",
            "CareerDiploma",
            "Career Diploma"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "DiplomaTypeDescriptor", CareerDiplomaTypeDescriptorUri),
            careerDiplomaTypeDescriptorDocumentId,
            diplomaTypeDescriptorResourceKeyId
        );

        var honorsDiplomaTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            diplomaTypeDescriptorResourceKeyId,
            "Ed-Fi:DiplomaTypeDescriptor",
            HonorsDiplomaTypeDescriptorUri,
            "uri://ed-fi.org/DiplomaTypeDescriptor",
            "HonorsDiploma",
            "Honors Diploma"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "DiplomaTypeDescriptor", HonorsDiplomaTypeDescriptorUri),
            honorsDiplomaTypeDescriptorDocumentId,
            diplomaTypeDescriptorResourceKeyId
        );

        var cumulativeGradePointAverageTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            gradePointAverageTypeDescriptorResourceKeyId,
            "Ed-Fi:GradePointAverageTypeDescriptor",
            CumulativeGradePointAverageTypeDescriptorUri,
            "uri://ed-fi.org/GradePointAverageTypeDescriptor",
            "Cumulative",
            "Cumulative"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "GradePointAverageTypeDescriptor",
                CumulativeGradePointAverageTypeDescriptorUri
            ),
            cumulativeGradePointAverageTypeDescriptorDocumentId,
            gradePointAverageTypeDescriptorResourceKeyId
        );

        var sessionGradePointAverageTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            gradePointAverageTypeDescriptorResourceKeyId,
            "Ed-Fi:GradePointAverageTypeDescriptor",
            SessionGradePointAverageTypeDescriptorUri,
            "uri://ed-fi.org/GradePointAverageTypeDescriptor",
            "Session",
            "Session"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "GradePointAverageTypeDescriptor",
                SessionGradePointAverageTypeDescriptorUri
            ),
            sessionGradePointAverageTypeDescriptorDocumentId,
            gradePointAverageTypeDescriptorResourceKeyId
        );

        var weightedGradePointAverageTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            gradePointAverageTypeDescriptorResourceKeyId,
            "Ed-Fi:GradePointAverageTypeDescriptor",
            WeightedGradePointAverageTypeDescriptorUri,
            "uri://ed-fi.org/GradePointAverageTypeDescriptor",
            "Weighted",
            "Weighted"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "GradePointAverageTypeDescriptor",
                WeightedGradePointAverageTypeDescriptorUri
            ),
            weightedGradePointAverageTypeDescriptorDocumentId,
            gradePointAverageTypeDescriptorResourceKeyId
        );

        var meritRecognitionTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            recognitionTypeDescriptorResourceKeyId,
            "Ed-Fi:RecognitionTypeDescriptor",
            MeritRecognitionTypeDescriptorUri,
            "uri://ed-fi.org/RecognitionTypeDescriptor",
            "Merit",
            "Merit"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "RecognitionTypeDescriptor",
                MeritRecognitionTypeDescriptorUri
            ),
            meritRecognitionTypeDescriptorDocumentId,
            recognitionTypeDescriptorResourceKeyId
        );

        var leadershipRecognitionTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            recognitionTypeDescriptorResourceKeyId,
            "Ed-Fi:RecognitionTypeDescriptor",
            LeadershipRecognitionTypeDescriptorUri,
            "uri://ed-fi.org/RecognitionTypeDescriptor",
            "Leadership",
            "Leadership"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "RecognitionTypeDescriptor",
                LeadershipRecognitionTypeDescriptorUri
            ),
            leadershipRecognitionTypeDescriptorDocumentId,
            recognitionTypeDescriptorResourceKeyId
        );

        var attendanceRecognitionTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            recognitionTypeDescriptorResourceKeyId,
            "Ed-Fi:RecognitionTypeDescriptor",
            AttendanceRecognitionTypeDescriptorUri,
            "uri://ed-fi.org/RecognitionTypeDescriptor",
            "Attendance",
            "Attendance"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "RecognitionTypeDescriptor",
                AttendanceRecognitionTypeDescriptorUri
            ),
            attendanceRecognitionTypeDescriptorDocumentId,
            recognitionTypeDescriptorResourceKeyId
        );

        return new(
            schoolDocumentId,
            schoolYearTypeDocumentId,
            studentDocumentId,
            fallTermDescriptorDocumentId,
            honorRollAcademicHonorCategoryDescriptorDocumentId,
            scholarAthleteAcademicHonorCategoryDescriptorDocumentId,
            communityServiceAcademicHonorCategoryDescriptorDocumentId,
            standardDiplomaTypeDescriptorDocumentId,
            careerDiplomaTypeDescriptorDocumentId,
            honorsDiplomaTypeDescriptorDocumentId,
            cumulativeGradePointAverageTypeDescriptorDocumentId,
            sessionGradePointAverageTypeDescriptorDocumentId,
            weightedGradePointAverageTypeDescriptorDocumentId,
            meritRecognitionTypeDescriptorDocumentId,
            leadershipRecognitionTypeDescriptorDocumentId,
            attendanceRecognitionTypeDescriptorDocumentId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
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

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
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
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertSchoolYearTypeAsync(
        long documentId,
        int schoolYear,
        bool currentSchoolYear,
        string schoolYearDescription
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentId",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", schoolYearDescription)
        );
    }

    private async Task InsertStudentAsync(
        long documentId,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private static ReferentialId CreateReferentialId(
        (string ProjectName, string ResourceName, bool IsDescriptor) targetResource,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                targetResource.IsDescriptor
            ),
            new DocumentIdentity([
                .. identityElements.Select(identityElement => new DocumentIdentityElement(
                    new JsonPath(identityElement.IdentityJsonPath),
                    identityElement.IdentityValue
                )),
            ])
        );
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return CreateReferentialId(
            (projectName, resourceName, true),
            (DocumentIdentity.DescriptorIdentityJsonPath.Value, descriptorUri.ToLowerInvariant())
        );
    }

    private int CalculateCreateCollectionInsertParameterCount()
    {
        var writePlan = _mappingSet.GetWritePlanOrThrow(StudentAcademicRecordResource);

        return GetTablePlan(
                writePlan,
                "StudentAcademicRecordAcademicHonor"
            ).BulkInsertBatching.ParametersPerRow * CreateAcademicHonorSpecs.Count
            + GetTablePlan(writePlan, "StudentAcademicRecordDiploma").BulkInsertBatching.ParametersPerRow
                * CreateDiplomaSpecs.Count
            + GetTablePlan(
                writePlan,
                "StudentAcademicRecordGradePointAverage"
            ).BulkInsertBatching.ParametersPerRow * CreateGradePointAverageSpecs.Count
            + GetTablePlan(writePlan, "StudentAcademicRecordRecognition").BulkInsertBatching.ParametersPerRow
                * CreateRecognitionSpecs.Count;
    }

    private static TableWritePlan GetTablePlan(ResourceWritePlan resourceWritePlan, string tableName)
    {
        return resourceWritePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.Table == new DbTableName(new DbSchemaName("edfi"), tableName)
        );
    }

    private async Task<AuthoritativeStudentAcademicRecordPersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            AcademicRecord: await ReadStudentAcademicRecordAsync(document.DocumentId),
            AcademicRecordExtension: await ReadStudentAcademicRecordExtensionAsync(document.DocumentId),
            AcademicHonors: await ReadAcademicHonorsAsync(document.DocumentId),
            Diplomas: await ReadDiplomasAsync(document.DocumentId),
            GradePointAverages: await ReadGradePointAveragesAsync(document.DocumentId),
            Recognitions: await ReadRecognitionsAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativePostAsUpdateDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new AuthoritativePostAsUpdateDocumentRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeStudentAcademicRecordRow> ReadStudentAcademicRecordAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "TermDescriptor_DescriptorId",
                "CumulativeEarnedCredits",
                TO_CHAR("ProjectedGraduationDate", 'YYYY-MM-DD') AS "ProjectedGraduationDate"
            FROM "edfi"."StudentAcademicRecord"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeStudentAcademicRecordRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "EducationOrganization_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EducationOrganization_EducationOrganizationId"
                ),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "SchoolYear_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(rows[0], "SchoolYear_SchoolYear"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Student_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "Student_StudentUniqueId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "TermDescriptor_DescriptorId"),
                PostAsUpdateIntegrationTestSupport.GetDecimal(rows[0], "CumulativeEarnedCredits"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "ProjectedGraduationDate")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentAcademicRecord row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeStudentAcademicRecordExtensionRow> ReadStudentAcademicRecordExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "Notes"
            FROM "sample"."StudentAcademicRecordExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeStudentAcademicRecordExtensionRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "Notes")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentAcademicRecordExtension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<
        IReadOnlyList<AuthoritativeStudentAcademicRecordAcademicHonorRow>
    > ReadAcademicHonorsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "AcademicHonorCategoryDescriptor_DescriptorId",
                "HonorDescription",
                "IssuerName"
            FROM "edfi"."StudentAcademicRecordAcademicHonor"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordAcademicHonorRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(
                    row,
                    "AcademicHonorCategoryDescriptor_DescriptorId"
                ),
                PostAsUpdateIntegrationTestSupport.GetString(row, "HonorDescription"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "IssuerName")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeStudentAcademicRecordDiplomaRow>> ReadDiplomasAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "DiplomaTypeDescriptor_DescriptorId",
                TO_CHAR("DiplomaAwardDate", 'YYYY-MM-DD') AS "DiplomaAwardDate",
                "DiplomaDescription"
            FROM "edfi"."StudentAcademicRecordDiploma"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordDiplomaRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "DiplomaTypeDescriptor_DescriptorId"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "DiplomaAwardDate"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "DiplomaDescription")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<AuthoritativeStudentAcademicRecordGradePointAverageRow>
    > ReadGradePointAveragesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "GradePointAverageTypeDescriptor_DescriptorId",
                "GradePointAverageValue",
                "IsCumulative"
            FROM "edfi"."StudentAcademicRecordGradePointAverage"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordGradePointAverageRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(
                    row,
                    "GradePointAverageTypeDescriptor_DescriptorId"
                ),
                PostAsUpdateIntegrationTestSupport.GetDecimal(row, "GradePointAverageValue"),
                PostAsUpdateIntegrationTestSupport.GetBoolean(row, "IsCumulative")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeStudentAcademicRecordRecognitionRow>> ReadRecognitionsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "RecognitionTypeDescriptor_DescriptorId",
                "RecognitionDescription",
                "IssuerName"
            FROM "edfi"."StudentAcademicRecordRecognition"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordRecognitionRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "RecognitionTypeDescriptor_DescriptorId"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "RecognitionDescription"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "IssuerName")
            ))
            .ToArray();
    }

    private IReadOnlyList<AuthoritativeStudentAcademicRecordAcademicHonorRow> CreateExpectedAcademicHonors(
        IReadOnlyList<AcademicHonorSpec> specs,
        IReadOnlyDictionary<string, long> collectionItemIdsByDescription,
        long documentId
    )
    {
        return specs
            .Select(
                (spec, ordinal) =>
                    new AuthoritativeStudentAcademicRecordAcademicHonorRow(
                        collectionItemIdsByDescription[spec.HonorDescription],
                        ordinal,
                        documentId,
                        ResolveAcademicHonorDescriptorId(spec.DescriptorUri),
                        spec.HonorDescription,
                        spec.IssuerName
                    )
            )
            .ToArray();
    }

    private IReadOnlyList<AuthoritativeStudentAcademicRecordDiplomaRow> CreateExpectedDiplomas(
        IReadOnlyList<DiplomaSpec> specs,
        IReadOnlyDictionary<string, long> collectionItemIdsByAwardDate,
        long documentId
    )
    {
        return specs
            .Select(
                (spec, ordinal) =>
                    new AuthoritativeStudentAcademicRecordDiplomaRow(
                        collectionItemIdsByAwardDate[spec.DiplomaAwardDate],
                        ordinal,
                        documentId,
                        ResolveDiplomaDescriptorId(spec.DescriptorUri),
                        spec.DiplomaAwardDate,
                        spec.DiplomaDescription
                    )
            )
            .ToArray();
    }

    private IReadOnlyList<AuthoritativeStudentAcademicRecordGradePointAverageRow> CreateExpectedGradePointAverages(
        IReadOnlyList<GradePointAverageSpec> specs,
        IReadOnlyDictionary<string, long> collectionItemIdsByKey,
        long documentId
    )
    {
        return specs
            .Select(
                (spec, ordinal) =>
                    new AuthoritativeStudentAcademicRecordGradePointAverageRow(
                        collectionItemIdsByKey[ResolveGradePointAverageKey(spec.DescriptorUri)],
                        ordinal,
                        documentId,
                        ResolveGradePointAverageDescriptorId(spec.DescriptorUri),
                        spec.GradePointAverageValue,
                        spec.IsCumulative
                    )
            )
            .ToArray();
    }

    private IReadOnlyList<AuthoritativeStudentAcademicRecordRecognitionRow> CreateExpectedRecognitions(
        IReadOnlyList<RecognitionSpec> specs,
        IReadOnlyDictionary<string, long> collectionItemIdsByKey,
        long documentId
    )
    {
        return specs
            .Select(
                (spec, ordinal) =>
                    new AuthoritativeStudentAcademicRecordRecognitionRow(
                        collectionItemIdsByKey[ResolveRecognitionKey(spec.DescriptorUri)],
                        ordinal,
                        documentId,
                        ResolveRecognitionDescriptorId(spec.DescriptorUri),
                        spec.RecognitionDescription,
                        spec.IssuerName
                    )
            )
            .ToArray();
    }

    private static IReadOnlyDictionary<string, long> CreateExpectedIdsByKey(
        IEnumerable<string> updateKeys,
        IReadOnlyDictionary<string, long> previousIdsByKey,
        IReadOnlyDictionary<string, long> currentIdsByKey
    )
    {
        Dictionary<string, long> idsByKey = new(StringComparer.Ordinal);

        foreach (var key in updateKeys)
        {
            idsByKey.Add(
                key,
                previousIdsByKey.TryGetValue(key, out var previousId) ? previousId : currentIdsByKey[key]
            );
        }

        return idsByKey;
    }

    private static IReadOnlyDictionary<string, long> CreateAcademicHonorIdsByDescription(
        IEnumerable<AuthoritativeStudentAcademicRecordAcademicHonorRow> rows
    ) => rows.ToDictionary(row => row.HonorDescription, row => row.CollectionItemId, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, long> CreateDiplomaIdsByAwardDate(
        IEnumerable<AuthoritativeStudentAcademicRecordDiplomaRow> rows
    ) => rows.ToDictionary(row => row.DiplomaAwardDate, row => row.CollectionItemId, StringComparer.Ordinal);

    private IReadOnlyDictionary<string, long> CreateGradePointAverageIdsByKey(
        IEnumerable<AuthoritativeStudentAcademicRecordGradePointAverageRow> rows
    ) =>
        rows.ToDictionary(
            row => ResolveGradePointAverageKey(row.GradePointAverageTypeDescriptorId),
            row => row.CollectionItemId,
            StringComparer.Ordinal
        );

    private IReadOnlyDictionary<string, long> CreateRecognitionIdsByKey(
        IEnumerable<AuthoritativeStudentAcademicRecordRecognitionRow> rows
    ) =>
        rows.ToDictionary(
            row => ResolveRecognitionKey(row.RecognitionTypeDescriptorId),
            row => row.CollectionItemId,
            StringComparer.Ordinal
        );

    private long ResolveAcademicHonorDescriptorId(string descriptorUri)
    {
        return descriptorUri switch
        {
            HonorRollAcademicHonorCategoryDescriptorUri =>
                _seedData.HonorRollAcademicHonorCategoryDescriptorDocumentId,
            ScholarAthleteAcademicHonorCategoryDescriptorUri =>
                _seedData.ScholarAthleteAcademicHonorCategoryDescriptorDocumentId,
            CommunityServiceAcademicHonorCategoryDescriptorUri =>
                _seedData.CommunityServiceAcademicHonorCategoryDescriptorDocumentId,
            _ => throw new InvalidOperationException(
                $"Unsupported academic honor category descriptor '{descriptorUri}'."
            ),
        };
    }

    private long ResolveDiplomaDescriptorId(string descriptorUri)
    {
        return descriptorUri switch
        {
            StandardDiplomaTypeDescriptorUri => _seedData.StandardDiplomaTypeDescriptorDocumentId,
            CareerDiplomaTypeDescriptorUri => _seedData.CareerDiplomaTypeDescriptorDocumentId,
            HonorsDiplomaTypeDescriptorUri => _seedData.HonorsDiplomaTypeDescriptorDocumentId,
            _ => throw new InvalidOperationException(
                $"Unsupported diploma type descriptor '{descriptorUri}'."
            ),
        };
    }

    private long ResolveGradePointAverageDescriptorId(string descriptorUri)
    {
        return descriptorUri switch
        {
            CumulativeGradePointAverageTypeDescriptorUri =>
                _seedData.CumulativeGradePointAverageTypeDescriptorDocumentId,
            SessionGradePointAverageTypeDescriptorUri =>
                _seedData.SessionGradePointAverageTypeDescriptorDocumentId,
            WeightedGradePointAverageTypeDescriptorUri =>
                _seedData.WeightedGradePointAverageTypeDescriptorDocumentId,
            _ => throw new InvalidOperationException(
                $"Unsupported grade point average type descriptor '{descriptorUri}'."
            ),
        };
    }

    private string ResolveGradePointAverageKey(long descriptorId)
    {
        return descriptorId switch
        {
            var value when value == _seedData.CumulativeGradePointAverageTypeDescriptorDocumentId =>
                "Cumulative",
            var value when value == _seedData.SessionGradePointAverageTypeDescriptorDocumentId => "Session",
            var value when value == _seedData.WeightedGradePointAverageTypeDescriptorDocumentId => "Weighted",
            _ => throw new InvalidOperationException(
                $"Unsupported grade point average descriptor id '{descriptorId}'."
            ),
        };
    }

    private static string ResolveGradePointAverageKey(string descriptorUri)
    {
        return descriptorUri switch
        {
            CumulativeGradePointAverageTypeDescriptorUri => "Cumulative",
            SessionGradePointAverageTypeDescriptorUri => "Session",
            WeightedGradePointAverageTypeDescriptorUri => "Weighted",
            _ => throw new InvalidOperationException(
                $"Unsupported grade point average descriptor '{descriptorUri}'."
            ),
        };
    }

    private long ResolveRecognitionDescriptorId(string descriptorUri)
    {
        return descriptorUri switch
        {
            MeritRecognitionTypeDescriptorUri => _seedData.MeritRecognitionTypeDescriptorDocumentId,
            LeadershipRecognitionTypeDescriptorUri => _seedData.LeadershipRecognitionTypeDescriptorDocumentId,
            AttendanceRecognitionTypeDescriptorUri => _seedData.AttendanceRecognitionTypeDescriptorDocumentId,
            _ => throw new InvalidOperationException(
                $"Unsupported recognition type descriptor '{descriptorUri}'."
            ),
        };
    }

    private string ResolveRecognitionKey(long descriptorId)
    {
        return descriptorId switch
        {
            var value when value == _seedData.MeritRecognitionTypeDescriptorDocumentId => "Merit",
            var value when value == _seedData.LeadershipRecognitionTypeDescriptorDocumentId => "Leadership",
            var value when value == _seedData.AttendanceRecognitionTypeDescriptorDocumentId => "Attendance",
            _ => throw new InvalidOperationException(
                $"Unsupported recognition descriptor id '{descriptorId}'."
            ),
        };
    }

    private static string ResolveRecognitionKey(string descriptorUri)
    {
        return descriptorUri switch
        {
            MeritRecognitionTypeDescriptorUri => "Merit",
            LeadershipRecognitionTypeDescriptorUri => "Leadership",
            AttendanceRecognitionTypeDescriptorUri => "Attendance",
            _ => throw new InvalidOperationException(
                $"Unsupported recognition descriptor '{descriptorUri}'."
            ),
        };
    }

    private static AcademicHonorSpec CreateAcademicHonorSpec(int index, bool isUpdated)
    {
        var descriptorUri = (index % 3) switch
        {
            1 => HonorRollAcademicHonorCategoryDescriptorUri,
            2 => ScholarAthleteAcademicHonorCategoryDescriptorUri,
            _ => CommunityServiceAcademicHonorCategoryDescriptorUri,
        };

        return new(
            descriptorUri,
            $"Honor {index:00}",
            isUpdated ? $"Updated Honors Board {index:00}" : $"Create Honors Board {index:00}"
        );
    }

    private static DiplomaSpec CreateDiplomaSpec(int index, bool isUpdated)
    {
        var descriptorUri = (index % 3) switch
        {
            1 => StandardDiplomaTypeDescriptorUri,
            2 => CareerDiplomaTypeDescriptorUri,
            _ => HonorsDiplomaTypeDescriptorUri,
        };
        var diplomaAwardDate = new DateOnly(2028, 5, 1)
            .AddDays(index - 1)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new(
            descriptorUri,
            diplomaAwardDate,
            isUpdated ? $"Updated Diploma Path {index:00}" : $"Diploma Path {index:00}"
        );
    }

    private sealed record AcademicHonorSpec(string DescriptorUri, string HonorDescription, string IssuerName);

    private sealed record DiplomaSpec(
        string DescriptorUri,
        string DiplomaAwardDate,
        string DiplomaDescription
    );

    private sealed record GradePointAverageSpec(
        string DescriptorUri,
        decimal GradePointAverageValue,
        bool IsCumulative,
        decimal MaxGradePointAverageValue
    );

    private sealed record RecognitionSpec(
        string DescriptorUri,
        string RecognitionDescription,
        string IssuerName
    );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const string CreateRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Austin"
            },
            {
              "city": "Dallas"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    private const string PostAsUpdateRequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin"
            },
            {
              "city": "Dallas"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1-Updated"
                    }
                  }
                },
                {}
              ]
            }
          }
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003")
    );
    private static readonly DocumentUuid IncomingPostAsUpdateDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private FocusedPostAsUpdateDocumentRow _documentBeforePostAsUpdate = null!;
    private FocusedPostAsUpdateDocumentRow _documentAfterPostAsUpdate = null!;
    private FocusedPostAsUpdateSchoolRow _schoolAfterPostAsUpdate = null!;
    private IReadOnlyList<FocusedPostAsUpdateSchoolAddressRow> _addressesBeforePostAsUpdate = null!;
    private IReadOnlyList<FocusedPostAsUpdateSchoolAddressRow> _addressesAfterPostAsUpdate = null!;
    private IReadOnlyList<FocusedPostAsUpdateSchoolExtensionAddressRow> _extensionAddressesBeforePostAsUpdate =
        null!;
    private IReadOnlyList<FocusedPostAsUpdateSchoolExtensionAddressRow> _extensionAddressesAfterPostAsUpdate =
        null!;
    private UpsertResult _postAsUpdateResult = null!;
    private ReferentialId _persistedSchoolReferentialId;
    private long _documentCount;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostAsUpdateIntegrationTestSupport.CreateServiceProvider();

        var createResult = await ExecuteUpsertAsync(
            CreateRequestBodyJson,
            ExistingSchoolDocumentUuid,
            "pg-post-as-update-create"
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        _documentBeforePostAsUpdate = await ReadDocumentAsync(ExistingSchoolDocumentUuid.Value);
        _addressesBeforePostAsUpdate = await ReadSchoolAddressesAsync(_documentBeforePostAsUpdate.DocumentId);
        _extensionAddressesBeforePostAsUpdate = await ReadSchoolExtensionAddressesAsync(
            _documentBeforePostAsUpdate.DocumentId
        );
        _persistedSchoolReferentialId = new ReferentialId(
            (
                await ReadReferentialIdentityRowAsync(
                    _documentBeforePostAsUpdate.DocumentId,
                    _mappingSet.ResourceKeyIdByResource[SchoolResource]
                )
            ).ReferentialId
        );

        _postAsUpdateResult = await ExecuteUpsertAsync(
            PostAsUpdateRequestBodyJson,
            IncomingPostAsUpdateDocumentUuid,
            "pg-post-as-update-existing-document",
            _persistedSchoolReferentialId
        );

        _documentAfterPostAsUpdate = await ReadDocumentAsync(ExistingSchoolDocumentUuid.Value);
        _schoolAfterPostAsUpdate = await ReadSchoolAsync(_documentAfterPostAsUpdate.DocumentId);
        _addressesAfterPostAsUpdate = await ReadSchoolAddressesAsync(_documentAfterPostAsUpdate.DocumentId);
        _extensionAddressesAfterPostAsUpdate = await ReadSchoolExtensionAddressesAsync(
            _documentAfterPostAsUpdate.DocumentId
        );
        _documentCount = await ReadDocumentCountAsync();
        _incomingDocumentUuidCount = await ReadDocumentCountAsync(IncomingPostAsUpdateDocumentUuid.Value);
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_returns_update_success_and_preserves_the_existing_document_row_for_post_as_update()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolDocumentUuid);
        _documentAfterPostAsUpdate.DocumentUuid.Should().Be(ExistingSchoolDocumentUuid.Value);
        _documentAfterPostAsUpdate
            .ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[SchoolResource]);
        _documentAfterPostAsUpdate
            .ContentVersion.Should()
            .BeGreaterThan(_documentBeforePostAsUpdate.ContentVersion);
        _documentCount.Should().Be(1);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_applies_changed_full_surface_state_without_inserting_new_rows_for_post_as_update()
    {
        _addressesBeforePostAsUpdate.Should().HaveCount(2);
        _extensionAddressesBeforePostAsUpdate.Should().HaveCount(2);

        _schoolAfterPostAsUpdate
            .Should()
            .Be(new FocusedPostAsUpdateSchoolRow(_documentAfterPostAsUpdate.DocumentId, 255901, null));

        _addressesAfterPostAsUpdate
            .Should()
            .Equal(
                new FocusedPostAsUpdateSchoolAddressRow(
                    _addressesBeforePostAsUpdate[0].CollectionItemId,
                    _documentAfterPostAsUpdate.DocumentId,
                    0,
                    "Austin"
                ),
                new FocusedPostAsUpdateSchoolAddressRow(
                    _addressesBeforePostAsUpdate[1].CollectionItemId,
                    _documentAfterPostAsUpdate.DocumentId,
                    1,
                    "Dallas"
                )
            );

        _extensionAddressesAfterPostAsUpdate
            .Should()
            .Equal(
                new FocusedPostAsUpdateSchoolExtensionAddressRow(
                    _addressesBeforePostAsUpdate[0].CollectionItemId,
                    _documentAfterPostAsUpdate.DocumentId,
                    "Zone-1-Updated"
                )
            );
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId = null
    )
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWritePostAsUpdateFocused",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            CreateUpsertRequest(requestBodyJson, documentUuid, traceId, referentialId)
        );
    }

    private UpsertRequest CreateUpsertRequest(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(referentialId),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    private static DocumentInfo CreateSchoolDocumentInfo(ReferentialId? referentialId = null)
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private async Task<FocusedPostAsUpdateDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new FocusedPostAsUpdateDocumentRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<long> ReadDocumentCountAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document";
            """
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<ReferentialIdentityRow> ReadReferentialIdentityRowAsync(
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
                AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new ReferentialIdentityRow(
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "ReferentialId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }

    private async Task<long> ReadDocumentCountAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<FocusedPostAsUpdateSchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId", "ShortName"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new FocusedPostAsUpdateSchoolRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "SchoolId"),
                PostAsUpdateIntegrationTestSupport.GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<FocusedPostAsUpdateSchoolAddressRow>> ReadSchoolAddressesAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new FocusedPostAsUpdateSchoolAddressRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "City")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<FocusedPostAsUpdateSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "BaseCollectionItemId", "School_DocumentId", "Zone"
            FROM "sample"."SchoolExtensionAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "BaseCollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new FocusedPostAsUpdateSchoolExtensionAddressRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "BaseCollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "Zone")
            ))
            .ToArray();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Post_Create_Race_With_The_Focused_Stable_Key_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const string CreateWinnerRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "CREATE-WINNER",
          "addresses": [
            {
              "city": "Austin"
            }
          ]
        }
        """;

    private const string StaleCreateCandidateRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LAST-WRITER",
          "addresses": [
            {
              "city": "Dallas"
            }
          ]
        }
        """;

    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid CreateWinnerDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000101")
    );
    private static readonly DocumentUuid StaleCreateCandidateDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000102")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ConcurrentPostCreateRaceCoordinator _raceCoordinator = null!;
    private UpsertResult _createWinnerResult = null!;
    private UpsertResult _staleCreateCandidateResult = null!;
    private ReferentialId _sharedSchoolReferentialId;
    private FocusedPostAsUpdateDocumentRow _documentAfterRequests = null!;
    private FocusedPostAsUpdateSchoolRow _schoolAfterRequests = null!;
    private IReadOnlyList<FocusedPostAsUpdateSchoolAddressRow> _addressesAfterRequests = null!;
    private long _documentCount;
    private long _staleCreateCandidateDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _raceCoordinator = new ConcurrentPostCreateRaceCoordinator();
        _serviceProvider = PostAsUpdateIntegrationTestSupport.CreateServiceProvider(_raceCoordinator);
        _sharedSchoolReferentialId = new ReferentialId(await ComputeSchoolReferentialIdAsync());

        var staleCreateCandidateTask = ExecuteUpsertAsync(
            StaleCreateCandidateRequestBodyJson,
            StaleCreateCandidateDocumentUuid,
            "pg-post-create-race-stale-candidate",
            _sharedSchoolReferentialId
        );

        await _raceCoordinator.WaitUntilFirstResolverCallIsPendingAsync();

        _createWinnerResult = await ExecuteUpsertAsync(
            CreateWinnerRequestBodyJson,
            CreateWinnerDocumentUuid,
            "pg-post-create-race-create-winner",
            _sharedSchoolReferentialId
        );

        (await ReadDocumentCountAsync(CreateWinnerDocumentUuid.Value)).Should().Be(1);
        (await ReadReferentialIdentityCountAsync(_sharedSchoolReferentialId.Value)).Should().Be(1);

        _raceCoordinator.ReleaseFirstResolverCall();

        _staleCreateCandidateResult = await staleCreateCandidateTask;
        _documentAfterRequests = await ReadDocumentAsync(CreateWinnerDocumentUuid.Value);
        _schoolAfterRequests = await ReadSchoolAsync(_documentAfterRequests.DocumentId);
        _addressesAfterRequests = await ReadSchoolAddressesAsync(_documentAfterRequests.DocumentId);
        _documentCount = await ReadDocumentCountAsync();
        _staleCreateCandidateDocumentUuidCount = await ReadDocumentCountAsync(
            StaleCreateCandidateDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        _raceCoordinator?.ReleaseFirstResolverCall();

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
    public void It_converts_the_stale_create_candidate_into_post_as_update_after_the_competing_create_commits()
    {
        _createWinnerResult.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(CreateWinnerDocumentUuid));
        _staleCreateCandidateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _staleCreateCandidateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(CreateWinnerDocumentUuid);
        _documentAfterRequests.DocumentUuid.Should().Be(CreateWinnerDocumentUuid.Value);
        _documentCount.Should().Be(1);
        _staleCreateCandidateDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_applies_last_writer_state_to_the_existing_document_instead_of_creating_duplicate_rows()
    {
        _schoolAfterRequests
            .Should()
            .Be(new FocusedPostAsUpdateSchoolRow(_documentAfterRequests.DocumentId, 255901, "LAST-WRITER"));
        _addressesAfterRequests.Should().ContainSingle();
        _addressesAfterRequests[0].SchoolDocumentId.Should().Be(_documentAfterRequests.DocumentId);
        _addressesAfterRequests[0].Ordinal.Should().Be(0);
        _addressesAfterRequests[0].City.Should().Be("Dallas");
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId = null
    )
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWritePostCreateRaceFocused",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            CreateUpsertRequest(requestBodyJson, documentUuid, traceId, referentialId)
        );
    }

    private UpsertRequest CreateUpsertRequest(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(referentialId),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    private static DocumentInfo CreateSchoolDocumentInfo(ReferentialId? referentialId = null)
    {
        var schoolIdentity = CreateSchoolIdentity();

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private static DocumentIdentity CreateSchoolIdentity() =>
        new([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]);

    private async Task<FocusedPostAsUpdateDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new FocusedPostAsUpdateDocumentRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<long> ReadDocumentCountAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document";
            """
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<long> ReadDocumentCountAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<long> ReadReferentialIdentityCountAsync(Guid referentialId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."ReferentialIdentity"
            WHERE "ReferentialId" = @referentialId;
            """,
            new NpgsqlParameter("referentialId", referentialId)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<Guid> ComputeSchoolReferentialIdAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "dms"."uuidv5"(
                'edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid,
                'Ed-FiSchool' || '$$.schoolId=' || @schoolId
            ) AS "ReferentialId";
            """,
            new NpgsqlParameter("schoolId", "255901")
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "ReferentialId")
            : throw new InvalidOperationException(
                $"Expected exactly one referential-id row, but found {rows.Count}."
            );
    }

    private async Task<FocusedPostAsUpdateSchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId", "ShortName"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new FocusedPostAsUpdateSchoolRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "SchoolId"),
                PostAsUpdateIntegrationTestSupport.GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<FocusedPostAsUpdateSchoolAddressRow>> ReadSchoolAddressesAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new FocusedPostAsUpdateSchoolAddressRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "City")
            ))
            .ToArray();
    }
}

internal sealed record AuthoritativePostAsUpdateDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record ReferentialIdentityRow(Guid ReferentialId, long DocumentId, short ResourceKeyId);

internal sealed record AuthoritativeSchoolYearTypeRow(
    long DocumentId,
    int SchoolYear,
    bool CurrentSchoolYear,
    string SchoolYearDescription
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Ds52_SchoolYearType_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private const string CreateRequestBodyJson = """
        {
          "schoolYear": 2026,
          "currentSchoolYear": true,
          "schoolYearDescription": "2025-2026"
        }
        """;

    private const string PostAsUpdateRequestBodyJson = """
        {
          "schoolYear": 2026,
          "currentSchoolYear": false,
          "schoolYearDescription": "2025-2026 Revised"
        }
        """;

    private static readonly QualifiedResourceName SchoolYearTypeResource = new("Ed-Fi", "SchoolYearType");
    private static readonly ResourceInfo SchoolYearTypeResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("SchoolYearType"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid ExistingSchoolYearTypeDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid IncomingSchoolYearTypeDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-000000000002")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private AuthoritativePostAsUpdateDocumentRow _documentBeforePostAsUpdate = null!;
    private AuthoritativePostAsUpdateDocumentRow _documentAfterPostAsUpdate = null!;
    private AuthoritativeSchoolYearTypeRow _schoolYearTypeAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private ReferentialId _persistedSchoolYearTypeReferentialId;
    private long _documentCount;
    private long _incomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostAsUpdateIntegrationTestSupport.CreateServiceProvider();

        var createResult = await ExecuteUpsertAsync(
            CreateRequestBodyJson,
            ExistingSchoolYearTypeDocumentUuid,
            "pg-authoritative-school-year-type-create"
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        _documentBeforePostAsUpdate = await ReadDocumentAsync(ExistingSchoolYearTypeDocumentUuid.Value);
        _persistedSchoolYearTypeReferentialId = new ReferentialId(
            (
                await ReadReferentialIdentityRowAsync(
                    _documentBeforePostAsUpdate.DocumentId,
                    _mappingSet.ResourceKeyIdByResource[SchoolYearTypeResource]
                )
            ).ReferentialId
        );

        _postAsUpdateResult = await ExecuteUpsertAsync(
            PostAsUpdateRequestBodyJson,
            IncomingSchoolYearTypeDocumentUuid,
            "pg-authoritative-school-year-type-post-as-update",
            _persistedSchoolYearTypeReferentialId
        );

        _documentAfterPostAsUpdate = await ReadDocumentAsync(ExistingSchoolYearTypeDocumentUuid.Value);
        _schoolYearTypeAfterPostAsUpdate = await ReadSchoolYearTypeAsync(
            _documentAfterPostAsUpdate.DocumentId
        );
        _documentCount = await ReadDocumentCountAsync();
        _incomingDocumentUuidCount = await ReadDocumentCountAsync(IncomingSchoolYearTypeDocumentUuid.Value);
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingSchoolYearTypeDocumentUuid);
        _documentAfterPostAsUpdate.DocumentUuid.Should().Be(ExistingSchoolYearTypeDocumentUuid.Value);
        _documentAfterPostAsUpdate
            .ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[SchoolYearTypeResource]);
        _documentAfterPostAsUpdate
            .ContentVersion.Should()
            .BeGreaterThan(_documentBeforePostAsUpdate.ContentVersion);
        _documentCount.Should().Be(1);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_updates_the_authoritative_ds52_row_in_place_for_post_as_update()
    {
        _schoolYearTypeAfterPostAsUpdate
            .Should()
            .Be(
                new AuthoritativeSchoolYearTypeRow(
                    _documentAfterPostAsUpdate.DocumentId,
                    2026,
                    false,
                    "2025-2026 Revised"
                )
            );
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId = null
    )
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWritePostAsUpdateAuthoritativeDs52",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            CreateUpsertRequest(requestBodyJson, documentUuid, traceId, referentialId)
        );
    }

    private UpsertRequest CreateUpsertRequest(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId
    ) =>
        new(
            ResourceInfo: SchoolYearTypeResourceInfo,
            DocumentInfo: CreateSchoolYearTypeDocumentInfo(referentialId),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    private static DocumentInfo CreateSchoolYearTypeDocumentInfo(ReferentialId? referentialId = null)
    {
        var schoolYearIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolYear"), "2026"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolYearIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolYearTypeResourceInfo, schoolYearIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private async Task<AuthoritativePostAsUpdateDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new AuthoritativePostAsUpdateDocumentRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<long> ReadDocumentCountAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document";
            """
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<long> ReadDocumentCountAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<AuthoritativeSchoolYearTypeRow> ReadSchoolYearTypeAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolYear", "CurrentSchoolYear", "SchoolYearDescription"
            FROM "edfi"."SchoolYearType"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSchoolYearTypeRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(rows[0], "SchoolYear"),
                PostAsUpdateIntegrationTestSupport.GetBoolean(rows[0], "CurrentSchoolYear"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "SchoolYearDescription")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one SchoolYearType row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<ReferentialIdentityRow> ReadReferentialIdentityRowAsync(
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
                AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new ReferentialIdentityRow(
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "ReferentialId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }
}

internal sealed record AuthoritativeStudentAcademicRecordSeedData(
    long SchoolDocumentId,
    long SchoolYearTypeDocumentId,
    long StudentDocumentId,
    long FallTermDescriptorDocumentId,
    long HonorRollAcademicHonorCategoryDescriptorDocumentId,
    long ScholarAthleteAcademicHonorCategoryDescriptorDocumentId,
    long CommunityServiceAcademicHonorCategoryDescriptorDocumentId,
    long StandardDiplomaTypeDescriptorDocumentId,
    long CareerDiplomaTypeDescriptorDocumentId,
    long HonorsDiplomaTypeDescriptorDocumentId,
    long CumulativeGradePointAverageTypeDescriptorDocumentId,
    long SessionGradePointAverageTypeDescriptorDocumentId,
    long WeightedGradePointAverageTypeDescriptorDocumentId,
    long MeritRecognitionTypeDescriptorDocumentId,
    long LeadershipRecognitionTypeDescriptorDocumentId,
    long AttendanceRecognitionTypeDescriptorDocumentId
);

internal sealed record AuthoritativeStudentAcademicRecordRow(
    long DocumentId,
    long EducationOrganizationDocumentId,
    long EducationOrganizationId,
    long SchoolYearDocumentId,
    int SchoolYear,
    long StudentDocumentId,
    string StudentUniqueId,
    long TermDescriptorId,
    decimal CumulativeEarnedCredits,
    string ProjectedGraduationDate
);

internal sealed record AuthoritativeStudentAcademicRecordExtensionRow(long DocumentId, string Notes);

internal sealed record AuthoritativeStudentAcademicRecordAcademicHonorRow(
    long CollectionItemId,
    int Ordinal,
    long StudentAcademicRecordDocumentId,
    long AcademicHonorCategoryDescriptorId,
    string HonorDescription,
    string IssuerName
);

internal sealed record AuthoritativeStudentAcademicRecordDiplomaRow(
    long CollectionItemId,
    int Ordinal,
    long StudentAcademicRecordDocumentId,
    long DiplomaTypeDescriptorId,
    string DiplomaAwardDate,
    string DiplomaDescription
);

internal sealed record AuthoritativeStudentAcademicRecordGradePointAverageRow(
    long CollectionItemId,
    int Ordinal,
    long StudentAcademicRecordDocumentId,
    long GradePointAverageTypeDescriptorId,
    decimal GradePointAverageValue,
    bool IsCumulative
);

internal sealed record AuthoritativeStudentAcademicRecordRecognitionRow(
    long CollectionItemId,
    int Ordinal,
    long StudentAcademicRecordDocumentId,
    long RecognitionTypeDescriptorId,
    string RecognitionDescription,
    string IssuerName
);

internal sealed record AuthoritativeStudentAcademicRecordPersistedState(
    AuthoritativePostAsUpdateDocumentRow Document,
    AuthoritativeStudentAcademicRecordRow AcademicRecord,
    AuthoritativeStudentAcademicRecordExtensionRow AcademicRecordExtension,
    IReadOnlyList<AuthoritativeStudentAcademicRecordAcademicHonorRow> AcademicHonors,
    IReadOnlyList<AuthoritativeStudentAcademicRecordDiplomaRow> Diplomas,
    IReadOnlyList<AuthoritativeStudentAcademicRecordGradePointAverageRow> GradePointAverages,
    IReadOnlyList<AuthoritativeStudentAcademicRecordRecognitionRow> Recognitions
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    private const string CreateRequestBodyJson = """
        {
          "educationOrganizationReference": {
            "educationOrganizationId": 100
          },
          "schoolYearTypeReference": {
            "schoolYear": 2026
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall",
          "cumulativeEarnedCredits": 18.500,
          "projectedGraduationDate": "2028-05-20",
          "academicHonors": [
            {
              "academicHonorCategoryDescriptor": "uri://ed-fi.org/AcademicHonorCategoryDescriptor#HonorRoll",
              "honorDescription": "Honor Roll",
              "issuerName": "Alpha Academy"
            },
            {
              "academicHonorCategoryDescriptor": "uri://ed-fi.org/AcademicHonorCategoryDescriptor#ScholarAthlete",
              "honorDescription": "Scholar Athlete",
              "issuerName": "Alpha Academy"
            }
          ],
          "diplomas": [
            {
              "diplomaTypeDescriptor": "uri://ed-fi.org/DiplomaTypeDescriptor#StandardDiploma",
              "diplomaAwardDate": "2028-05-24",
              "diplomaDescription": "Standard Path"
            },
            {
              "diplomaTypeDescriptor": "uri://ed-fi.org/DiplomaTypeDescriptor#CareerDiploma",
              "diplomaAwardDate": "2028-05-25",
              "diplomaDescription": "Career Path"
            }
          ],
          "gradePointAverages": [
            {
              "gradePointAverageTypeDescriptor": "uri://ed-fi.org/GradePointAverageTypeDescriptor#Cumulative",
              "gradePointAverageValue": 3.4500,
              "isCumulative": true,
              "maxGradePointAverageValue": 4.0000
            },
            {
              "gradePointAverageTypeDescriptor": "uri://ed-fi.org/GradePointAverageTypeDescriptor#Session",
              "gradePointAverageValue": 3.7000,
              "isCumulative": false,
              "maxGradePointAverageValue": 4.0000
            }
          ],
          "recognitions": [
            {
              "recognitionTypeDescriptor": "uri://ed-fi.org/RecognitionTypeDescriptor#Merit",
              "recognitionDescription": "Regional Merit",
              "issuerName": "State Board"
            },
            {
              "recognitionTypeDescriptor": "uri://ed-fi.org/RecognitionTypeDescriptor#Leadership",
              "recognitionDescription": "Leadership Award",
              "issuerName": "State Board"
            }
          ],
          "_ext": {
            "sample": {
              "notes": "Initial transcript note"
            }
          }
        }
        """;

    private const string PostAsUpdateRequestBodyJson = """
        {
          "educationOrganizationReference": {
            "educationOrganizationId": 100
          },
          "schoolYearTypeReference": {
            "schoolYear": 2026
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall",
          "cumulativeEarnedCredits": 19.250,
          "projectedGraduationDate": "2028-05-22",
          "academicHonors": [
            {
              "academicHonorCategoryDescriptor": "uri://ed-fi.org/AcademicHonorCategoryDescriptor#HonorRoll",
              "honorDescription": "Honor Roll",
              "issuerName": "District Honors Board"
            },
            {
              "academicHonorCategoryDescriptor": "uri://ed-fi.org/AcademicHonorCategoryDescriptor#CommunityService",
              "honorDescription": "Community Service",
              "issuerName": "Community Foundation"
            }
          ],
          "diplomas": [
            {
              "diplomaTypeDescriptor": "uri://ed-fi.org/DiplomaTypeDescriptor#StandardDiploma",
              "diplomaAwardDate": "2028-05-24",
              "diplomaDescription": "Revised Standard Path"
            },
            {
              "diplomaTypeDescriptor": "uri://ed-fi.org/DiplomaTypeDescriptor#HonorsDiploma",
              "diplomaAwardDate": "2028-05-26",
              "diplomaDescription": "Honors Path"
            }
          ],
          "gradePointAverages": [
            {
              "gradePointAverageTypeDescriptor": "uri://ed-fi.org/GradePointAverageTypeDescriptor#Cumulative",
              "gradePointAverageValue": 3.6100,
              "isCumulative": true,
              "maxGradePointAverageValue": 4.0000
            },
            {
              "gradePointAverageTypeDescriptor": "uri://ed-fi.org/GradePointAverageTypeDescriptor#Weighted",
              "gradePointAverageValue": 4.1200,
              "isCumulative": false,
              "maxGradePointAverageValue": 5.0000
            }
          ],
          "recognitions": [
            {
              "recognitionTypeDescriptor": "uri://ed-fi.org/RecognitionTypeDescriptor#Merit",
              "recognitionDescription": "State Merit",
              "issuerName": "State Board"
            },
            {
              "recognitionTypeDescriptor": "uri://ed-fi.org/RecognitionTypeDescriptor#Attendance",
              "recognitionDescription": "Perfect Attendance",
              "issuerName": "District Office"
            }
          ],
          "_ext": {
            "sample": {
              "notes": "Updated transcript note"
            }
          }
        }
        """;

    private static readonly QualifiedResourceName StudentAcademicRecordResource = new(
        "Ed-Fi",
        "StudentAcademicRecord"
    );
    private static readonly DocumentUuid ExistingStudentAcademicRecordDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid IncomingStudentAcademicRecordDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000002")
    );
    private static readonly DocumentUuid RepeatStudentAcademicRecordDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000003")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private ResourceSchema _extensionResourceSchema = null!;
    private AuthoritativeStudentAcademicRecordSeedData _seedData = null!;
    private AuthoritativeStudentAcademicRecordPersistedState _stateAfterCreate = null!;
    private AuthoritativeStudentAcademicRecordPersistedState _stateAfterPostAsUpdate = null!;
    private AuthoritativeStudentAcademicRecordPersistedState _stateAfterRepeatPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private UpsertResult _repeatPostAsUpdateResult = null!;
    private ReferentialId _persistedStudentAcademicRecordReferentialId;
    private long _resourceDocumentCount;
    private long _incomingDocumentUuidCount;
    private long _repeatIncomingDocumentUuidCount;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = PostAsUpdateIntegrationTestSupport.CreateServiceProvider();

        var (baseProjectSchema, baseResourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "StudentAcademicRecord"
        );
        var (_, extensionResourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "sample",
            "StudentAcademicRecord"
        );

        _resourceInfo = CreateResourceInfo(baseProjectSchema, baseResourceSchema);
        _baseResourceSchema = baseResourceSchema;
        _extensionResourceSchema = extensionResourceSchema;
        _seedData = await SeedReferenceDataAsync();

        var createResult = await ExecuteUpsertAsync(
            CreateRequestBodyJson,
            ExistingStudentAcademicRecordDocumentUuid,
            "pg-authoritative-sample-student-academic-record-create"
        );

        if (createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail($"Create reference failure: {FormatReferenceFailure(createReferenceFailure)}");
        }

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(ExistingStudentAcademicRecordDocumentUuid.Value);
        _persistedStudentAcademicRecordReferentialId = new ReferentialId(
            (
                await ReadReferentialIdentityRowAsync(
                    _stateAfterCreate.Document.DocumentId,
                    _mappingSet.ResourceKeyIdByResource[StudentAcademicRecordResource]
                )
            ).ReferentialId
        );

        _postAsUpdateResult = await ExecuteUpsertAsync(
            PostAsUpdateRequestBodyJson,
            IncomingStudentAcademicRecordDocumentUuid,
            "pg-authoritative-sample-student-academic-record-post-as-update",
            _persistedStudentAcademicRecordReferentialId
        );

        if (_postAsUpdateResult is UpsertResult.UpsertFailureReference postAsUpdateReferenceFailure)
        {
            Assert.Fail(
                $"POST-as-update reference failure: {FormatReferenceFailure(postAsUpdateReferenceFailure)}"
            );
        }

        _stateAfterPostAsUpdate = await ReadPersistedStateAsync(
            ExistingStudentAcademicRecordDocumentUuid.Value
        );
        _repeatPostAsUpdateResult = await ExecuteUpsertAsync(
            PostAsUpdateRequestBodyJson,
            RepeatStudentAcademicRecordDocumentUuid,
            "pg-authoritative-sample-student-academic-record-repeat-post-as-update",
            _persistedStudentAcademicRecordReferentialId
        );

        if (_repeatPostAsUpdateResult is UpsertResult.UpsertFailureReference repeatPostReferenceFailure)
        {
            Assert.Fail(
                $"Repeat POST-as-update reference failure: {FormatReferenceFailure(repeatPostReferenceFailure)}"
            );
        }

        _stateAfterRepeatPostAsUpdate = await ReadPersistedStateAsync(
            ExistingStudentAcademicRecordDocumentUuid.Value
        );
        _resourceDocumentCount = await ReadDocumentCountAsync(
            _mappingSet.ResourceKeyIdByResource[StudentAcademicRecordResource]
        );
        _incomingDocumentUuidCount = await ReadDocumentCountAsync(
            IncomingStudentAcademicRecordDocumentUuid.Value
        );
        _repeatIncomingDocumentUuidCount = await ReadDocumentCountAsync(
            RepeatStudentAcademicRecordDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
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
    public void It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingStudentAcademicRecordDocumentUuid);
        _stateAfterPostAsUpdate
            .Document.DocumentUuid.Should()
            .Be(ExistingStudentAcademicRecordDocumentUuid.Value);
        _stateAfterPostAsUpdate.Document.DocumentId.Should().Be(_stateAfterCreate.Document.DocumentId);
        _stateAfterPostAsUpdate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[StudentAcademicRecordResource]);
        _stateAfterPostAsUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
        _resourceDocumentCount.Should().Be(1);
        _incomingDocumentUuidCount.Should().Be(0);
    }

    [Test]
    public void It_updates_root_and_extension_rows_in_place_for_authoritative_student_academic_record_post_as_update()
    {
        _stateAfterPostAsUpdate
            .AcademicRecord.Should()
            .Be(
                new AuthoritativeStudentAcademicRecordRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.SchoolDocumentId,
                    100,
                    _seedData.SchoolYearTypeDocumentId,
                    2026,
                    _seedData.StudentDocumentId,
                    "10001",
                    _seedData.FallTermDescriptorDocumentId,
                    19.250m,
                    "2028-05-22"
                )
            );
        _stateAfterPostAsUpdate
            .AcademicRecordExtension.Should()
            .Be(
                new AuthoritativeStudentAcademicRecordExtensionRow(
                    _stateAfterCreate.Document.DocumentId,
                    "Updated transcript note"
                )
            );
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_for_retained_child_rows_and_replaces_omitted_rows()
    {
        _stateAfterPostAsUpdate
            .AcademicHonors.Should()
            .Equal(
                new AuthoritativeStudentAcademicRecordAcademicHonorRow(
                    _stateAfterCreate.AcademicHonors[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.HonorRollAcademicHonorCategoryDescriptorDocumentId,
                    "Honor Roll",
                    "District Honors Board"
                ),
                new AuthoritativeStudentAcademicRecordAcademicHonorRow(
                    _stateAfterPostAsUpdate.AcademicHonors[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.CommunityServiceAcademicHonorCategoryDescriptorDocumentId,
                    "Community Service",
                    "Community Foundation"
                )
            );
        _stateAfterPostAsUpdate
            .AcademicHonors[1]
            .CollectionItemId.Should()
            .NotBe(_stateAfterCreate.AcademicHonors[1].CollectionItemId);
        _stateAfterPostAsUpdate
            .AcademicHonors.Select(row => row.CollectionItemId)
            .Should()
            .NotContain(_stateAfterCreate.AcademicHonors[1].CollectionItemId);

        _stateAfterPostAsUpdate
            .Diplomas.Should()
            .Equal(
                new AuthoritativeStudentAcademicRecordDiplomaRow(
                    _stateAfterCreate.Diplomas[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.StandardDiplomaTypeDescriptorDocumentId,
                    "2028-05-24",
                    "Revised Standard Path"
                ),
                new AuthoritativeStudentAcademicRecordDiplomaRow(
                    _stateAfterPostAsUpdate.Diplomas[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.HonorsDiplomaTypeDescriptorDocumentId,
                    "2028-05-26",
                    "Honors Path"
                )
            );
        _stateAfterPostAsUpdate
            .Diplomas[1]
            .CollectionItemId.Should()
            .NotBe(_stateAfterCreate.Diplomas[1].CollectionItemId);
        _stateAfterPostAsUpdate
            .Diplomas.Select(row => row.CollectionItemId)
            .Should()
            .NotContain(_stateAfterCreate.Diplomas[1].CollectionItemId);

        _stateAfterPostAsUpdate
            .GradePointAverages.Should()
            .Equal(
                new AuthoritativeStudentAcademicRecordGradePointAverageRow(
                    _stateAfterCreate.GradePointAverages[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.CumulativeGradePointAverageTypeDescriptorDocumentId,
                    3.6100m,
                    true
                ),
                new AuthoritativeStudentAcademicRecordGradePointAverageRow(
                    _stateAfterPostAsUpdate.GradePointAverages[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.WeightedGradePointAverageTypeDescriptorDocumentId,
                    4.1200m,
                    false
                )
            );
        _stateAfterPostAsUpdate
            .GradePointAverages[1]
            .CollectionItemId.Should()
            .NotBe(_stateAfterCreate.GradePointAverages[1].CollectionItemId);
        _stateAfterPostAsUpdate
            .GradePointAverages.Select(row => row.CollectionItemId)
            .Should()
            .NotContain(_stateAfterCreate.GradePointAverages[1].CollectionItemId);

        _stateAfterPostAsUpdate
            .Recognitions.Should()
            .Equal(
                new AuthoritativeStudentAcademicRecordRecognitionRow(
                    _stateAfterCreate.Recognitions[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.MeritRecognitionTypeDescriptorDocumentId,
                    "State Merit",
                    "State Board"
                ),
                new AuthoritativeStudentAcademicRecordRecognitionRow(
                    _stateAfterPostAsUpdate.Recognitions[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.AttendanceRecognitionTypeDescriptorDocumentId,
                    "Perfect Attendance",
                    "District Office"
                )
            );
        _stateAfterPostAsUpdate
            .Recognitions[1]
            .CollectionItemId.Should()
            .NotBe(_stateAfterCreate.Recognitions[1].CollectionItemId);
        _stateAfterPostAsUpdate
            .Recognitions.Select(row => row.CollectionItemId)
            .Should()
            .NotContain(_stateAfterCreate.Recognitions[1].CollectionItemId);
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_authoritative_post_as_update()
    {
        _repeatPostAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _repeatPostAsUpdateResult
            .As<UpsertResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ExistingStudentAcademicRecordDocumentUuid);
        _stateAfterRepeatPostAsUpdate.Should().BeEquivalentTo(_stateAfterPostAsUpdate);
        _repeatIncomingDocumentUuidCount.Should().Be(0);
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId = null
    )
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWritePostAsUpdateAuthoritativeSampleStudentAcademicRecord",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            CreateUpsertRequest(requestBodyJson, documentUuid, traceId, referentialId)
        );
    }

    private UpsertRequest CreateUpsertRequest(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId? referentialId
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;

        return new(
            ResourceInfo: _resourceInfo,
            DocumentInfo: CreateDocumentInfo(requestBody, referentialId),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostAsUpdateNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostAsUpdateAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    private DocumentInfo CreateDocumentInfo(JsonNode requestBody, ReferentialId? referentialId = null)
    {
        var (documentIdentity, superclassIdentity) = _baseResourceSchema.ExtractIdentities(
            requestBody,
            NullLogger.Instance
        );

        var (baseDocumentReferences, baseDocumentReferenceArrays) = _baseResourceSchema.ExtractReferences(
            requestBody,
            NullLogger.Instance
        );

        var (extensionDocumentReferences, extensionDocumentReferenceArrays) =
            _extensionResourceSchema.ExtractReferences(requestBody, NullLogger.Instance);

        var descriptorReferences = _baseResourceSchema
            .ExtractDescriptors(requestBody, NullLogger.Instance)
            .Concat(_extensionResourceSchema.ExtractDescriptors(requestBody, NullLogger.Instance))
            .ToArray();

        return new(
            DocumentIdentity: documentIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(_resourceInfo, documentIdentity),
            DocumentReferences: [.. baseDocumentReferences, .. extensionDocumentReferences],
            DocumentReferenceArrays: [.. baseDocumentReferenceArrays, .. extensionDocumentReferenceArrays],
            DescriptorReferences: descriptorReferences,
            SuperclassIdentity: superclassIdentity
        );
    }

    private static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        var effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(
                project.ProjectEndpointName,
                projectEndpointName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project '{projectEndpointName}'."
            );

        return (projectSchema, new ResourceSchema(resourceSchemaNode));
    }

    private static ResourceInfo CreateResourceInfo(
        ProjectSchema projectSchema,
        ResourceSchema resourceSchema
    ) =>
        new(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

    private static string FormatReferenceFailure(UpsertResult.UpsertFailureReference failure)
    {
        var documentFailures = failure.InvalidDocumentReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );
        var descriptorFailures = failure.InvalidDescriptorReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );

        return string.Join(" | ", documentFailures.Concat(descriptorFailures));
    }

    private async Task<AuthoritativeStudentAcademicRecordSeedData> SeedReferenceDataAsync()
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");
        var academicHonorCategoryDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "AcademicHonorCategoryDescriptor"
        );
        var diplomaTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "DiplomaTypeDescriptor"
        );
        var gradePointAverageTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GradePointAverageTypeDescriptor"
        );
        var recognitionTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "RecognitionTypeDescriptor"
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000001"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, 100, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "School", false), ("$.schoolId", "100")),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", "100")
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000002"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, 2026, true, "2025-2026");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "SchoolYearType", false), ("$.schoolYear", "2026")),
            schoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-0000-0000-0000-000000000003"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, "10001", "Casey", "Cole");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", "10001")),
            studentDocumentId,
            studentResourceKeyId
        );

        var fallTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("44444444-0000-0000-0000-000000000004"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Fall",
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "TermDescriptor", "uri://ed-fi.org/TermDescriptor#Fall"),
            fallTermDescriptorDocumentId,
            termDescriptorResourceKeyId
        );

        var honorRollAcademicHonorCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000005"),
            academicHonorCategoryDescriptorResourceKeyId,
            "Ed-Fi:AcademicHonorCategoryDescriptor",
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor#HonorRoll",
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor",
            "HonorRoll",
            "Honor Roll"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AcademicHonorCategoryDescriptor",
                "uri://ed-fi.org/AcademicHonorCategoryDescriptor#HonorRoll"
            ),
            honorRollAcademicHonorCategoryDescriptorDocumentId,
            academicHonorCategoryDescriptorResourceKeyId
        );

        var scholarAthleteAcademicHonorCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000006"),
            academicHonorCategoryDescriptorResourceKeyId,
            "Ed-Fi:AcademicHonorCategoryDescriptor",
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor#ScholarAthlete",
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor",
            "ScholarAthlete",
            "Scholar Athlete"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AcademicHonorCategoryDescriptor",
                "uri://ed-fi.org/AcademicHonorCategoryDescriptor#ScholarAthlete"
            ),
            scholarAthleteAcademicHonorCategoryDescriptorDocumentId,
            academicHonorCategoryDescriptorResourceKeyId
        );

        var communityServiceAcademicHonorCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000007"),
            academicHonorCategoryDescriptorResourceKeyId,
            "Ed-Fi:AcademicHonorCategoryDescriptor",
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor#CommunityService",
            "uri://ed-fi.org/AcademicHonorCategoryDescriptor",
            "CommunityService",
            "Community Service"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AcademicHonorCategoryDescriptor",
                "uri://ed-fi.org/AcademicHonorCategoryDescriptor#CommunityService"
            ),
            communityServiceAcademicHonorCategoryDescriptorDocumentId,
            academicHonorCategoryDescriptorResourceKeyId
        );

        var standardDiplomaTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000008"),
            diplomaTypeDescriptorResourceKeyId,
            "Ed-Fi:DiplomaTypeDescriptor",
            "uri://ed-fi.org/DiplomaTypeDescriptor#StandardDiploma",
            "uri://ed-fi.org/DiplomaTypeDescriptor",
            "StandardDiploma",
            "Standard Diploma"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "DiplomaTypeDescriptor",
                "uri://ed-fi.org/DiplomaTypeDescriptor#StandardDiploma"
            ),
            standardDiplomaTypeDescriptorDocumentId,
            diplomaTypeDescriptorResourceKeyId
        );

        var careerDiplomaTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000009"),
            diplomaTypeDescriptorResourceKeyId,
            "Ed-Fi:DiplomaTypeDescriptor",
            "uri://ed-fi.org/DiplomaTypeDescriptor#CareerDiploma",
            "uri://ed-fi.org/DiplomaTypeDescriptor",
            "CareerDiploma",
            "Career Diploma"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "DiplomaTypeDescriptor",
                "uri://ed-fi.org/DiplomaTypeDescriptor#CareerDiploma"
            ),
            careerDiplomaTypeDescriptorDocumentId,
            diplomaTypeDescriptorResourceKeyId
        );

        var honorsDiplomaTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"),
            diplomaTypeDescriptorResourceKeyId,
            "Ed-Fi:DiplomaTypeDescriptor",
            "uri://ed-fi.org/DiplomaTypeDescriptor#HonorsDiploma",
            "uri://ed-fi.org/DiplomaTypeDescriptor",
            "HonorsDiploma",
            "Honors Diploma"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "DiplomaTypeDescriptor",
                "uri://ed-fi.org/DiplomaTypeDescriptor#HonorsDiploma"
            ),
            honorsDiplomaTypeDescriptorDocumentId,
            diplomaTypeDescriptorResourceKeyId
        );

        var cumulativeGradePointAverageTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b"),
            gradePointAverageTypeDescriptorResourceKeyId,
            "Ed-Fi:GradePointAverageTypeDescriptor",
            "uri://ed-fi.org/GradePointAverageTypeDescriptor#Cumulative",
            "uri://ed-fi.org/GradePointAverageTypeDescriptor",
            "Cumulative",
            "Cumulative"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "GradePointAverageTypeDescriptor",
                "uri://ed-fi.org/GradePointAverageTypeDescriptor#Cumulative"
            ),
            cumulativeGradePointAverageTypeDescriptorDocumentId,
            gradePointAverageTypeDescriptorResourceKeyId
        );

        var sessionGradePointAverageTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("cccccccc-0000-0000-0000-00000000000c"),
            gradePointAverageTypeDescriptorResourceKeyId,
            "Ed-Fi:GradePointAverageTypeDescriptor",
            "uri://ed-fi.org/GradePointAverageTypeDescriptor#Session",
            "uri://ed-fi.org/GradePointAverageTypeDescriptor",
            "Session",
            "Session"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "GradePointAverageTypeDescriptor",
                "uri://ed-fi.org/GradePointAverageTypeDescriptor#Session"
            ),
            sessionGradePointAverageTypeDescriptorDocumentId,
            gradePointAverageTypeDescriptorResourceKeyId
        );

        var weightedGradePointAverageTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("dddddddd-0000-0000-0000-00000000000d"),
            gradePointAverageTypeDescriptorResourceKeyId,
            "Ed-Fi:GradePointAverageTypeDescriptor",
            "uri://ed-fi.org/GradePointAverageTypeDescriptor#Weighted",
            "uri://ed-fi.org/GradePointAverageTypeDescriptor",
            "Weighted",
            "Weighted"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "GradePointAverageTypeDescriptor",
                "uri://ed-fi.org/GradePointAverageTypeDescriptor#Weighted"
            ),
            weightedGradePointAverageTypeDescriptorDocumentId,
            gradePointAverageTypeDescriptorResourceKeyId
        );

        var meritRecognitionTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("eeeeeeee-0000-0000-0000-00000000000e"),
            recognitionTypeDescriptorResourceKeyId,
            "Ed-Fi:RecognitionTypeDescriptor",
            "uri://ed-fi.org/RecognitionTypeDescriptor#Merit",
            "uri://ed-fi.org/RecognitionTypeDescriptor",
            "Merit",
            "Merit"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "RecognitionTypeDescriptor",
                "uri://ed-fi.org/RecognitionTypeDescriptor#Merit"
            ),
            meritRecognitionTypeDescriptorDocumentId,
            recognitionTypeDescriptorResourceKeyId
        );

        var leadershipRecognitionTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("ffffffff-0000-0000-0000-00000000000f"),
            recognitionTypeDescriptorResourceKeyId,
            "Ed-Fi:RecognitionTypeDescriptor",
            "uri://ed-fi.org/RecognitionTypeDescriptor#Leadership",
            "uri://ed-fi.org/RecognitionTypeDescriptor",
            "Leadership",
            "Leadership"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "RecognitionTypeDescriptor",
                "uri://ed-fi.org/RecognitionTypeDescriptor#Leadership"
            ),
            leadershipRecognitionTypeDescriptorDocumentId,
            recognitionTypeDescriptorResourceKeyId
        );

        var attendanceRecognitionTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("12121212-0000-0000-0000-000000000010"),
            recognitionTypeDescriptorResourceKeyId,
            "Ed-Fi:RecognitionTypeDescriptor",
            "uri://ed-fi.org/RecognitionTypeDescriptor#Attendance",
            "uri://ed-fi.org/RecognitionTypeDescriptor",
            "Attendance",
            "Attendance"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "RecognitionTypeDescriptor",
                "uri://ed-fi.org/RecognitionTypeDescriptor#Attendance"
            ),
            attendanceRecognitionTypeDescriptorDocumentId,
            recognitionTypeDescriptorResourceKeyId
        );

        return new(
            schoolDocumentId,
            schoolYearTypeDocumentId,
            studentDocumentId,
            fallTermDescriptorDocumentId,
            honorRollAcademicHonorCategoryDescriptorDocumentId,
            scholarAthleteAcademicHonorCategoryDescriptorDocumentId,
            communityServiceAcademicHonorCategoryDescriptorDocumentId,
            standardDiplomaTypeDescriptorDocumentId,
            careerDiplomaTypeDescriptorDocumentId,
            honorsDiplomaTypeDescriptorDocumentId,
            cumulativeGradePointAverageTypeDescriptorDocumentId,
            sessionGradePointAverageTypeDescriptorDocumentId,
            weightedGradePointAverageTypeDescriptorDocumentId,
            meritRecognitionTypeDescriptorDocumentId,
            leadershipRecognitionTypeDescriptorDocumentId,
            attendanceRecognitionTypeDescriptorDocumentId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
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

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
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
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertSchoolYearTypeAsync(
        long documentId,
        int schoolYear,
        bool currentSchoolYear,
        string schoolYearDescription
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentId",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", schoolYearDescription)
        );
    }

    private async Task InsertStudentAsync(
        long documentId,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private static ReferentialId CreateReferentialId(
        (string ProjectName, string ResourceName, bool IsDescriptor) targetResource,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                targetResource.IsDescriptor
            ),
            new DocumentIdentity([
                .. identityElements.Select(identityElement => new DocumentIdentityElement(
                    new JsonPath(identityElement.IdentityJsonPath),
                    identityElement.IdentityValue
                )),
            ])
        );
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return CreateReferentialId(
            (projectName, resourceName, true),
            (DocumentIdentity.DescriptorIdentityJsonPath.Value, descriptorUri.ToLowerInvariant())
        );
    }

    private async Task<AuthoritativeStudentAcademicRecordPersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            AcademicRecord: await ReadStudentAcademicRecordAsync(document.DocumentId),
            AcademicRecordExtension: await ReadStudentAcademicRecordExtensionAsync(document.DocumentId),
            AcademicHonors: await ReadAcademicHonorsAsync(document.DocumentId),
            Diplomas: await ReadDiplomasAsync(document.DocumentId),
            GradePointAverages: await ReadGradePointAveragesAsync(document.DocumentId),
            Recognitions: await ReadRecognitionsAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativePostAsUpdateDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new AuthoritativePostAsUpdateDocumentRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeStudentAcademicRecordRow> ReadStudentAcademicRecordAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "TermDescriptor_DescriptorId",
                "CumulativeEarnedCredits",
                TO_CHAR("ProjectedGraduationDate", 'YYYY-MM-DD') AS "ProjectedGraduationDate"
            FROM "edfi"."StudentAcademicRecord"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeStudentAcademicRecordRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "EducationOrganization_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EducationOrganization_EducationOrganizationId"
                ),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "SchoolYear_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(rows[0], "SchoolYear_SchoolYear"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Student_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "Student_StudentUniqueId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "TermDescriptor_DescriptorId"),
                PostAsUpdateIntegrationTestSupport.GetDecimal(rows[0], "CumulativeEarnedCredits"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "ProjectedGraduationDate")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentAcademicRecord row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeStudentAcademicRecordExtensionRow> ReadStudentAcademicRecordExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "Notes"
            FROM "sample"."StudentAcademicRecordExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeStudentAcademicRecordExtensionRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetString(rows[0], "Notes")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentAcademicRecordExtension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<
        IReadOnlyList<AuthoritativeStudentAcademicRecordAcademicHonorRow>
    > ReadAcademicHonorsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "AcademicHonorCategoryDescriptor_DescriptorId",
                "HonorDescription",
                "IssuerName"
            FROM "edfi"."StudentAcademicRecordAcademicHonor"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordAcademicHonorRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(
                    row,
                    "AcademicHonorCategoryDescriptor_DescriptorId"
                ),
                PostAsUpdateIntegrationTestSupport.GetString(row, "HonorDescription"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "IssuerName")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeStudentAcademicRecordDiplomaRow>> ReadDiplomasAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "DiplomaTypeDescriptor_DescriptorId",
                TO_CHAR("DiplomaAwardDate", 'YYYY-MM-DD') AS "DiplomaAwardDate",
                "DiplomaDescription"
            FROM "edfi"."StudentAcademicRecordDiploma"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordDiplomaRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "DiplomaTypeDescriptor_DescriptorId"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "DiplomaAwardDate"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "DiplomaDescription")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<AuthoritativeStudentAcademicRecordGradePointAverageRow>
    > ReadGradePointAveragesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "GradePointAverageTypeDescriptor_DescriptorId",
                "GradePointAverageValue",
                "IsCumulative"
            FROM "edfi"."StudentAcademicRecordGradePointAverage"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordGradePointAverageRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(
                    row,
                    "GradePointAverageTypeDescriptor_DescriptorId"
                ),
                PostAsUpdateIntegrationTestSupport.GetDecimal(row, "GradePointAverageValue"),
                PostAsUpdateIntegrationTestSupport.GetBoolean(row, "IsCumulative")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeStudentAcademicRecordRecognitionRow>> ReadRecognitionsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentAcademicRecord_DocumentId",
                "RecognitionTypeDescriptor_DescriptorId",
                "RecognitionDescription",
                "IssuerName"
            FROM "edfi"."StudentAcademicRecordRecognition"
            WHERE "StudentAcademicRecord_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeStudentAcademicRecordRecognitionRow(
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                PostAsUpdateIntegrationTestSupport.GetInt32(row, "Ordinal"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "StudentAcademicRecord_DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(row, "RecognitionTypeDescriptor_DescriptorId"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "RecognitionDescription"),
                PostAsUpdateIntegrationTestSupport.GetString(row, "IssuerName")
            ))
            .ToArray();
    }

    private async Task<ReferentialIdentityRow> ReadReferentialIdentityRowAsync(
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
              AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new ReferentialIdentityRow(
                PostAsUpdateIntegrationTestSupport.GetGuid(rows[0], "ReferentialId"),
                PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                PostAsUpdateIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
            );
    }

    private async Task<long> ReadDocumentCountAsync(short resourceKeyId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    private async Task<long> ReadDocumentCountAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? PostAsUpdateIntegrationTestSupport.GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }
}
