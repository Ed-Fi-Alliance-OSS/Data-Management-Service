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

file sealed class MssqlCollectionReorderAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlCollectionReorderUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed record MssqlCollectionReorderDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MssqlCollectionReorderSchoolRow(long DocumentId, long SchoolId, string? ShortName);

internal sealed record MssqlCollectionReorderSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record MssqlCollectionReorderSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

internal sealed record MssqlCollectionReorderPersistedState(
    MssqlCollectionReorderDocumentRow Document,
    MssqlCollectionReorderSchoolRow School,
    IReadOnlyList<MssqlCollectionReorderSchoolAddressRow> Addresses,
    IReadOnlyList<MssqlCollectionReorderSchoolExtensionAddressRow> ExtensionAddresses
);

file static class MssqlCollectionReorderIntegrationTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public const string CreateRequestBodyJson = """
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

    public const string UpdateRequestBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            {
              "city": "Dallas"
            },
            {
              "city": "Austin"
            }
          ],
          "_ext": {
            "sample": {
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                }
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

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(CreateRequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlCollectionReorderUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlCollectionReorderAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(UpdateRequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlCollectionReorderUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlCollectionReorderAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

    public static async Task<MssqlCollectionReorderPersistedState> ReadPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);
        var extensionAddresses = await ReadSchoolExtensionAddressesAsync(database, document.DocumentId);

        return new MssqlCollectionReorderPersistedState(document, school, addresses, extensionAddresses);
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

    public static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    public static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value as string
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    private static DocumentInfo CreateSchoolDocumentInfo()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    private static async Task<MssqlCollectionReorderDocumentRow> ReadDocumentAsync(
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
            ? new MssqlCollectionReorderDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<MssqlCollectionReorderSchoolRow> ReadSchoolAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [SchoolId], [ShortName]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new MssqlCollectionReorderSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<IReadOnlyList<MssqlCollectionReorderSchoolAddressRow>> ReadSchoolAddressesAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
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

        return rows.Select(row => new MssqlCollectionReorderSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<MssqlCollectionReorderSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [BaseCollectionItemId], [School_DocumentId], [Zone]
            FROM [sample].[SchoolExtensionAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [BaseCollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlCollectionReorderSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
            ))
            .ToArray();
    }

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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000011")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlCollectionReorderPersistedState _stateBeforeUpdate = null!;
    private MssqlCollectionReorderPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlCollectionReorderIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlCollectionReorderIntegrationTestSupport.CreateServiceProvider();

        await ExecuteCreateAsync();
        _stateBeforeUpdate = await MssqlCollectionReorderIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteUpdateAsync();
        _stateAfterUpdate = await MssqlCollectionReorderIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public void It_returns_update_success_and_bumps_content_version_for_a_full_surface_reorder()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
        _stateAfterUpdate.Document.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        _stateAfterUpdate
            .Document.ResourceKeyId.Should()
            .Be(
                _mappingSet.ResourceKeyIdByResource[
                    MssqlCollectionReorderIntegrationTestSupport.SchoolResource
                ]
            );
        _stateAfterUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateBeforeUpdate.Document.ContentVersion);
    }

    [Test]
    public void It_reuses_collection_item_ids_while_recomputing_ordinals_for_a_full_surface_reorder()
    {
        _stateBeforeUpdate.Addresses.Should().HaveCount(2);
        _stateAfterUpdate
            .School.Should()
            .Be(new MssqlCollectionReorderSchoolRow(_stateAfterUpdate.Document.DocumentId, 255901, "LHS"));

        _stateAfterUpdate
            .Addresses.Should()
            .Equal(
                new MssqlCollectionReorderSchoolAddressRow(
                    _stateBeforeUpdate.Addresses[1].CollectionItemId,
                    _stateAfterUpdate.Document.DocumentId,
                    0,
                    "Dallas"
                ),
                new MssqlCollectionReorderSchoolAddressRow(
                    _stateBeforeUpdate.Addresses[0].CollectionItemId,
                    _stateAfterUpdate.Document.DocumentId,
                    1,
                    "Austin"
                )
            );

        _stateAfterUpdate
            .ExtensionAddresses.Should()
            .Equal(
                new MssqlCollectionReorderSchoolExtensionAddressRow(
                    _stateBeforeUpdate.Addresses[0].CollectionItemId,
                    _stateAfterUpdate.Document.DocumentId,
                    "Zone-1"
                ),
                new MssqlCollectionReorderSchoolExtensionAddressRow(
                    _stateBeforeUpdate.Addresses[1].CollectionItemId,
                    _stateAfterUpdate.Document.DocumentId,
                    "Zone-2"
                )
            );
    }

    [Test]
    public void It_succeeds_for_a_two_row_swap_under_the_db_sibling_ordinal_uniqueness_constraint()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterUpdate.Addresses.Should().HaveCount(2);
        _stateAfterUpdate.Addresses.Select(static row => row.Ordinal).Should().Equal(0, 1);
        _stateAfterUpdate.Addresses.Select(static row => row.Ordinal).Should().OnlyHaveUniqueItems();
        _stateAfterUpdate.Addresses.Select(static row => row.City).Should().Equal("Dallas", "Austin");
    }

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(
            MssqlCollectionReorderIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-full-surface-reorder-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteCollectionReorder",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlCollectionReorderIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-full-surface-reorder-update"
            )
        );
    }
}
