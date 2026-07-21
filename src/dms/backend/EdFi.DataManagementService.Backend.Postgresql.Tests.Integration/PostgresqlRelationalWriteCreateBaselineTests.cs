// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.NoProfileCreateBaselineScenarios;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// PostgreSQL adapter for the shared <c>NoProfileFullSurfaceCreate</c> scenario. This suite owns only
/// the PostgreSQL provisioning, resolver registration, no-profile production-boundary invocation, and
/// SQL readback; the request body, persisted-state snapshot shapes, and behavioral assertions come
/// from <see cref="NoProfileCreateBaselineScenarios"/> in Backend.Tests.Common.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture
{
    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _result = null!;
    private PersistedDocumentRow _persistedDocument = null!;
    private PersistedSchoolRow _persistedSchool = null!;
    private IReadOnlyList<PersistedSchoolAddressRow> _persistedAddresses = null!;
    private IReadOnlyList<PersistedSchoolAddressPeriodRow> _persistedAddressPeriods = null!;
    private PersistedSchoolExtensionRow _persistedSchoolExtension = null!;
    private IReadOnlyList<PersistedSchoolExtensionAddressRow> _persistedExtensionAddresses = null!;
    private IReadOnlyList<PersistedSchoolExtensionInterventionRow> _persistedInterventions = null!;
    private IReadOnlyList<PersistedSchoolExtensionInterventionVisitRow> _persistedInterventionVisits = null!;
    private DateTimeOffset _lastModifiedAtAfterCreate;
    private GetResult _getResult = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();

        _result = await ExecuteCreateAsync();

        _persistedDocument = await ReadDocumentAsync();
        _persistedSchool = await ReadSchoolAsync(_persistedDocument.DocumentId);
        _persistedAddresses = await ReadSchoolAddressesAsync(_persistedDocument.DocumentId);
        _persistedAddressPeriods = await ReadSchoolAddressPeriodsAsync(_persistedDocument.DocumentId);
        _persistedSchoolExtension = await ReadSchoolExtensionAsync(_persistedDocument.DocumentId);
        _persistedExtensionAddresses = await ReadSchoolExtensionAddressesAsync(_persistedDocument.DocumentId);
        _persistedInterventions = await ReadSchoolExtensionInterventionsAsync(_persistedDocument.DocumentId);
        _persistedInterventionVisits = await ReadSchoolExtensionInterventionVisitsAsync(
            _persistedDocument.DocumentId
        );
        _lastModifiedAtAfterCreate = await ReadContentLastModifiedAtAsync();
        _getResult = await ExecuteGetByIdAsync();
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

    [Test]
    public void It_returns_insert_success_for_the_repository_create_flow() =>
        AssertInsertSuccess(_result, _mappingSet, _persistedDocument);

    [Test]
    public void It_persists_root_and_nested_collection_rows_with_stable_collection_ids() =>
        AssertRootAndNestedCollectionRows(
            _persistedDocument,
            _persistedSchool,
            _persistedAddresses,
            _persistedAddressPeriods
        );

    [Test]
    public void It_persists_root_extensions_collection_extensions_and_extension_child_collections() =>
        AssertRootAndCollectionExtensionAndExtensionChildRows(
            _persistedDocument,
            _persistedAddresses,
            _persistedSchoolExtension,
            _persistedExtensionAddresses,
            _persistedInterventions,
            _persistedInterventionVisits
        );

    [Test]
    public void It_reconstitutes_the_full_surface_document_via_relational_get_by_id() =>
        AssertFullSurfaceDocumentReconstitutes(
            _result,
            _getResult,
            _mappingSet,
            _lastModifiedAtAfterCreate,
            _persistedDocument.ContentVersion
        );

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteCreateBaseline",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(CreateUpsertRequest());
    }

    private UpsertRequest CreateUpsertRequest() =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: CreateRequestBody(),
            Headers: [],
            TraceId: new TraceId("no-profile-create-baseline"),
            DocumentUuid: SchoolDocumentUuid
        );

    private async Task<GetResult> ExecuteGetByIdAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteCreateBaseline",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.GetDocumentById(
            new IntegrationRelationalGetRequest(
                DocumentUuid: SchoolDocumentUuid,
                ResourceInfo: SchoolResourceInfo,
                MappingSet: _mappingSet,
                AuthorizationStrategyEvaluators: [],
                TraceId: new TraceId("no-profile-create-baseline-get")
            )
        );
    }

    private async Task<DateTimeOffset> ReadContentLastModifiedAtAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ContentLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", SchoolDocumentUuid.Value)
        );

        return rows.Count == 1
            ? GetDateTimeOffset(rows[0], "ContentLastModifiedAt")
            : throw new InvalidOperationException(
                $"Expected exactly one document metadata row for '{SchoolDocumentUuid.Value}', but found {rows.Count}."
            );
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private async Task<PersistedDocumentRow> ReadDocumentAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", SchoolDocumentUuid.Value)
        );

        return rows.Count == 1
            ? new PersistedDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{SchoolDocumentUuid.Value}', but found {rows.Count}."
            );
    }

    private async Task<PersistedSchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new PersistedSchoolRow(GetInt64(rows[0], "DocumentId"), GetInt64(rows[0], "SchoolId"))
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<PersistedSchoolAddressRow>> ReadSchoolAddressesAsync(long documentId)
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

        return rows.Select(row => new PersistedSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<PersistedSchoolAddressPeriodRow>> ReadSchoolAddressPeriodsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "PeriodName"
            FROM "edfi"."SchoolAddressPeriod"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolAddressPeriodRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "PeriodName")
            ))
            .ToArray();
    }

    private async Task<PersistedSchoolExtensionRow> ReadSchoolExtensionAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "CampusCode"
            FROM "sample"."SchoolExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new PersistedSchoolExtensionRow(
                GetInt64(rows[0], "DocumentId"),
                GetString(rows[0], "CampusCode")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school extension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<PersistedSchoolExtensionAddressRow>> ReadSchoolExtensionAddressesAsync(
        long documentId
    )
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

        return rows.Select(row => new PersistedSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<PersistedSchoolExtensionInterventionRow>
    > ReadSchoolExtensionInterventionsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "InterventionCode"
            FROM "sample"."SchoolExtensionIntervention"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolExtensionInterventionRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "InterventionCode")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<PersistedSchoolExtensionInterventionVisitRow>
    > ReadSchoolExtensionInterventionVisitsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "VisitCode"
            FROM "sample"."SchoolExtensionInterventionVisit"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolExtensionInterventionVisitRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "VisitCode")
            ))
            .ToArray();
    }

    private static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    private static DateTimeOffset GetDateTimeOffset(
        IReadOnlyDictionary<string, object?> row,
        string columnName
    ) =>
        GetRequiredValue(row, columnName) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc),
                TimeSpan.Zero
            ),
            _ => throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateTimeOffset value."
            ),
        };

    private static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

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
