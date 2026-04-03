// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
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

file static class PostAsUpdateIntegrationTestSupport
{
    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, PostAsUpdateNoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddScoped<RelationalDocumentStoreRepository>();
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
