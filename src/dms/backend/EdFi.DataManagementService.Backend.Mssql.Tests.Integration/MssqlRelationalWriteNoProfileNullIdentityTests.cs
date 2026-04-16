// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlNoProfileNullIdentityAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlNoProfileNullIdentityNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed record MssqlNoProfileNullIdentityDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MssqlNoProfileNullIdentitySchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? City
);

file static class MssqlNoProfileNullIdentityIntegrationTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    /// <summary>
    /// Seed body with one address whose city is "Seed-City".
    /// The test will SQL-UPDATE the City column to NULL after seeding.
    /// </summary>
    public const string CreateRequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Seed-City"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {}
              ]
            }
          }
        }
        """;

    /// <summary>
    /// Update body with one address whose city key is omitted — this represents an
    /// "absent" request-side semantic identity that should match the stored NULL.
    /// </summary>
    public const string UpdateRequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {}
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {}
              ]
            }
          }
        }
        """;

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

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

    public static UpsertRequest CreateUpsertRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string requestBodyJson,
        string traceId,
        ReferentialId? referentialId = null
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        var documentInfo = new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: referentialId
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        return new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlNoProfileNullIdentityNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlNoProfileNullIdentityAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string requestBodyJson,
        string traceId
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        var documentInfo = new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        return new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlNoProfileNullIdentityNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlNoProfileNullIdentityAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    public static async Task<MssqlNoProfileNullIdentityDocumentRow> ReadDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new MssqlNoProfileNullIdentityDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    public static async Task<
        IReadOnlyList<MssqlNoProfileNullIdentitySchoolAddressRow>
    > ReadSchoolAddressesAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [School_DocumentId], [Ordinal], [City]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlNoProfileNullIdentitySchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "City")
            ))
            .ToArray();
    }

    public static async Task<Guid> ReadReferentialIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [ReferentialId]
            FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId
                AND [ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? GetGuid(rows[0], "ReferentialId")
            : throw new InvalidOperationException(
                $"Expected exactly one referential identity row for document id '{documentId}' and resource key '{resourceKeyId}', but found {rows.Count}."
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

/// <summary>
/// Test 1: A changed PUT matches a null-City row and updates in place.
///
/// Seeds a School with one SchoolAddress(city="Seed-City"), then SQL-UPDATEs City to NULL.
/// A PUT with the address's city key omitted should match the stored NULL-identity row
/// in place (same CollectionItemId), proving that the no-profile synthetic adapter
/// correctly emits IsPresent:false for a stored SQL NULL semantic-identity member.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_No_Profile_Null_Identity_Changed_Put
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-000000000005")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _seededCollectionItemId;
    private UpdateResult _updateResult = null!;
    private IReadOnlyList<MssqlNoProfileNullIdentitySchoolAddressRow> _addressesAfterUpdate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlNoProfileNullIdentityIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlNoProfileNullIdentityIntegrationTestSupport.CreateServiceProvider();

        // Step 1: Create the document with city = "Seed-City"
        var createResult = await ExecuteUpsertAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateRequestBodyJson,
            "mssql-null-identity-changed-put-create"
        );
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var documentAfterCreate = await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadDocumentAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        var addressesAfterCreate =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadSchoolAddressesAsync(
                _database,
                documentAfterCreate.DocumentId
            );
        addressesAfterCreate.Should().HaveCount(1);
        _seededCollectionItemId = addressesAfterCreate[0].CollectionItemId;

        // Step 2: SQL-UPDATE City to NULL
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [City] = NULL
            WHERE [CollectionItemId] = @id;
            """,
            new SqlParameter("@id", _seededCollectionItemId)
        );
        rowsAffected.Should().Be(1);

        // Step 3: PUT with city omitted
        _updateResult = await ExecuteUpdateAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.UpdateRequestBodyJson,
            "mssql-null-identity-changed-put-update"
        );

        _addressesAfterUpdate =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadSchoolAddressesAsync(
                _database,
                documentAfterCreate.DocumentId
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
    public void It_returns_update_success()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public void It_has_exactly_one_address_row_after_the_put()
    {
        _addressesAfterUpdate.Should().HaveCount(1);
    }

    [Test]
    public void It_preserves_the_same_collection_item_id()
    {
        _addressesAfterUpdate[0].CollectionItemId.Should().Be(_seededCollectionItemId);
    }

    [Test]
    public void It_keeps_city_null_after_the_put()
    {
        _addressesAfterUpdate[0].City.Should().BeNull();
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(string requestBodyJson, string traceId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlNoProfileNullIdentityChangedPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateUpsertRequest(
                _mappingSet,
                SchoolDocumentUuid,
                requestBodyJson,
                traceId
            )
        );
    }

    private async Task<UpdateResult> ExecuteUpdateAsync(string requestBodyJson, string traceId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlNoProfileNullIdentityChangedPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                requestBodyJson,
                traceId
            )
        );
    }
}

/// <summary>
/// Test 2: A guarded-no-op PUT on the null-City row short-circuits.
///
/// Seeds the same initial state as Test 1 (city seeded then SQL-mutated to NULL),
/// then does a first PUT with city omitted to bring the full stored state (relational
/// rows + DocumentCache) into consistency. A second PUT with the same body should
/// be detected as a no-op: ContentVersion does not advance.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_No_Profile_Null_Identity_Guarded_No_Op_Put
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-000000000006")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _seededCollectionItemId;
    private long _contentVersionBeforeNoOpPut;
    private UpdateResult _noOpUpdateResult = null!;
    private MssqlNoProfileNullIdentityDocumentRow _documentAfterNoOpPut = null!;
    private IReadOnlyList<MssqlNoProfileNullIdentitySchoolAddressRow> _addressesAfterNoOpPut = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlNoProfileNullIdentityIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlNoProfileNullIdentityIntegrationTestSupport.CreateServiceProvider();

        // Step 1: Create document with city = "Seed-City"
        var createResult = await ExecuteUpsertAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateRequestBodyJson,
            "mssql-null-identity-no-op-create"
        );
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var documentAfterCreate = await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadDocumentAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        var addressesAfterCreate =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadSchoolAddressesAsync(
                _database,
                documentAfterCreate.DocumentId
            );
        addressesAfterCreate.Should().HaveCount(1);
        _seededCollectionItemId = addressesAfterCreate[0].CollectionItemId;

        // Step 2: SQL-UPDATE City to NULL
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [City] = NULL
            WHERE [CollectionItemId] = @id;
            """,
            new SqlParameter("@id", _seededCollectionItemId)
        );
        rowsAffected.Should().Be(1);

        // Step 3: First PUT with city omitted — drives the null-identity through the
        // executor so that relational rows, DocumentCache, and all stored state are
        // brought into consistency with City=NULL.
        var firstUpdateResult = await ExecuteUpdateAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.UpdateRequestBodyJson,
            "mssql-null-identity-no-op-first-update"
        );
        firstUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

        // Step 4: Capture ContentVersion after the consistency-establishing PUT.
        var documentBeforeNoOpPut = await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadDocumentAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        _contentVersionBeforeNoOpPut = documentBeforeNoOpPut.ContentVersion;

        // Step 5: Second PUT with the identical body — this should be a guarded no-op.
        _noOpUpdateResult = await ExecuteUpdateAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.UpdateRequestBodyJson,
            "mssql-null-identity-no-op-second-update"
        );

        _documentAfterNoOpPut = await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadDocumentAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        _addressesAfterNoOpPut =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadSchoolAddressesAsync(
                _database,
                _documentAfterNoOpPut.DocumentId
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
    public void It_returns_update_success()
    {
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public void It_does_not_advance_content_version()
    {
        _documentAfterNoOpPut.ContentVersion.Should().Be(_contentVersionBeforeNoOpPut);
    }

    [Test]
    public void It_has_exactly_one_address_row()
    {
        _addressesAfterNoOpPut.Should().HaveCount(1);
    }

    [Test]
    public void It_preserves_the_same_collection_item_id()
    {
        _addressesAfterNoOpPut[0].CollectionItemId.Should().Be(_seededCollectionItemId);
    }

    [Test]
    public void It_keeps_city_null()
    {
        _addressesAfterNoOpPut[0].City.Should().BeNull();
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(string requestBodyJson, string traceId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlNoProfileNullIdentityGuardedNoOp",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateUpsertRequest(
                _mappingSet,
                SchoolDocumentUuid,
                requestBodyJson,
                traceId
            )
        );
    }

    private async Task<UpdateResult> ExecuteUpdateAsync(string requestBodyJson, string traceId)
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlNoProfileNullIdentityGuardedNoOp",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                requestBodyJson,
                traceId
            )
        );
    }
}

/// <summary>
/// Test 3: A POST-as-update on the null-City row matches in place.
///
/// Seeds the same initial state (city seeded then SQL-mutated to NULL).
/// A POST (upsert) with the same schoolId (so the executor resolves to the existing
/// document) and the same single address with city omitted should detect this as
/// POST-as-update and flow through the update path, matching the null-identity row
/// in place.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_No_Profile_Null_Identity_Post_As_Update
{
    private static readonly DocumentUuid ExistingSchoolDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-000000000007")
    );

    private static readonly DocumentUuid IncomingPostAsUpdateDocumentUuid = new(
        Guid.Parse("cccccccc-0000-0000-0000-000000000008")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _seededCollectionItemId;
    private UpsertResult _postAsUpdateResult = null!;
    private IReadOnlyList<MssqlNoProfileNullIdentitySchoolAddressRow> _addressesAfterPostAsUpdate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlNoProfileNullIdentityIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlNoProfileNullIdentityIntegrationTestSupport.CreateServiceProvider();

        // Step 1: Create document with city = "Seed-City"
        var createResult = await ExecuteUpsertAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateRequestBodyJson,
            ExistingSchoolDocumentUuid,
            "mssql-null-identity-post-as-update-create"
        );
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var documentAfterCreate = await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadDocumentAsync(
            _database,
            ExistingSchoolDocumentUuid.Value
        );
        var addressesAfterCreate =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadSchoolAddressesAsync(
                _database,
                documentAfterCreate.DocumentId
            );
        addressesAfterCreate.Should().HaveCount(1);
        _seededCollectionItemId = addressesAfterCreate[0].CollectionItemId;

        // Step 2: SQL-UPDATE City to NULL
        var rowsAffected = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [City] = NULL
            WHERE [CollectionItemId] = @id;
            """,
            new SqlParameter("@id", _seededCollectionItemId)
        );
        rowsAffected.Should().Be(1);

        // Step 3: Read the persisted ReferentialId (needed for the upsert to resolve
        // to the existing document rather than creating a new one)
        var referentialId = new ReferentialId(
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadReferentialIdAsync(
                _database,
                documentAfterCreate.DocumentId,
                _mappingSet.ResourceKeyIdByResource[
                    MssqlNoProfileNullIdentityIntegrationTestSupport.SchoolResource
                ]
            )
        );

        // Step 4: POST (upsert) with city omitted — should resolve as post-as-update
        _postAsUpdateResult = await ExecuteUpsertAsync(
            MssqlNoProfileNullIdentityIntegrationTestSupport.UpdateRequestBodyJson,
            IncomingPostAsUpdateDocumentUuid,
            "mssql-null-identity-post-as-update-upsert",
            referentialId
        );

        var documentAfterPostAsUpdate =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadDocumentAsync(
                _database,
                ExistingSchoolDocumentUuid.Value
            );
        _addressesAfterPostAsUpdate =
            await MssqlNoProfileNullIdentityIntegrationTestSupport.ReadSchoolAddressesAsync(
                _database,
                documentAfterPostAsUpdate.DocumentId
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
    public void It_returns_update_success()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    [Test]
    public void It_has_exactly_one_address_row()
    {
        _addressesAfterPostAsUpdate.Should().HaveCount(1);
    }

    [Test]
    public void It_preserves_the_same_collection_item_id()
    {
        _addressesAfterPostAsUpdate[0].CollectionItemId.Should().Be(_seededCollectionItemId);
    }

    [Test]
    public void It_keeps_city_null()
    {
        _addressesAfterPostAsUpdate[0].City.Should().BeNull();
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
                    InstanceName: "MssqlNoProfileNullIdentityPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(
            MssqlNoProfileNullIdentityIntegrationTestSupport.CreateUpsertRequest(
                _mappingSet,
                documentUuid,
                requestBodyJson,
                traceId,
                referentialId
            )
        );
    }
}
