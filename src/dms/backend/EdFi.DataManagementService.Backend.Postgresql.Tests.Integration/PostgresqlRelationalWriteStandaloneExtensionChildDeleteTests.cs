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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.NoProfileUpdateSemanticsScenarios;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// PostgreSQL proof that a changed PUT omitting a standalone extension-child collection deletes those
/// extension-child rows while leaving the root and base rows intact, through the existing no-profile
/// merge production boundary (parity scenario
/// <c>NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection</c>). This suite
/// owns only the PostgreSQL provisioning, resolver registration, production-boundary invocation, and
/// SQL readback; the request bodies, snapshot shapes, and assertions come from
/// <see cref="NoProfileUpdateSemanticsScenarios"/> in Backend.Tests.Common.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Changed_Put_Omitting_A_Standalone_Extension_Child_Collection
{
    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _updateResult = null!;
    private ExtensionChildSchoolRow _schoolBeforeUpdate = null!;
    private ExtensionChildSchoolRow _schoolAfterUpdate = null!;
    private IReadOnlyList<ExtensionChildAddressRow> _addressesBeforeUpdate = null!;
    private IReadOnlyList<ExtensionChildAddressRow> _addressesAfterUpdate = null!;
    private IReadOnlyList<ExtensionInterventionRow> _interventionsBeforeUpdate = null!;
    private IReadOnlyList<ExtensionInterventionVisitRow> _visitsBeforeUpdate = null!;
    private IReadOnlyList<ExtensionInterventionRow> _interventionsAfterUpdate = null!;
    private IReadOnlyList<ExtensionInterventionVisitRow> _visitsAfterUpdate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            StandaloneExtensionChildFixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();

        long documentId = await ExecuteCreateAsync();

        _schoolBeforeUpdate = await ReadSchoolAsync(documentId);
        _addressesBeforeUpdate = await ReadSchoolAddressesAsync(documentId);
        _interventionsBeforeUpdate = await ReadInterventionsAsync(documentId);
        _visitsBeforeUpdate = await ReadInterventionVisitsAsync(documentId);

        _updateResult = await ExecuteUpdateAsync();

        _schoolAfterUpdate = await ReadSchoolAsync(documentId);
        _addressesAfterUpdate = await ReadSchoolAddressesAsync(documentId);
        _interventionsAfterUpdate = await ReadInterventionsAsync(documentId);
        _visitsAfterUpdate = await ReadInterventionVisitsAsync(documentId);
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
    public void It_deletes_the_omitted_standalone_extension_child_collection_without_deleting_base_rows() =>
        AssertStandaloneExtensionChildCollectionDeleted(
            _updateResult,
            _schoolBeforeUpdate,
            _schoolAfterUpdate,
            _addressesBeforeUpdate,
            _addressesAfterUpdate,
            _interventionsBeforeUpdate,
            _visitsBeforeUpdate,
            _interventionsAfterUpdate,
            _visitsAfterUpdate
        );

    private async Task<long> ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlStandaloneExtensionChildDelete",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(CreateUpsertRequest());
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        return await ReadDocumentIdAsync();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlStandaloneExtensionChildDelete",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(CreateUpdateRequest());
    }

    private UpsertRequest CreateUpsertRequest() =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: StandaloneExtensionChildCreateBody(),
            Headers: [],
            TraceId: new TraceId("no-profile-standalone-extension-child-create"),
            DocumentUuid: StandaloneExtensionChildDocumentUuid
        );

    private UpdateRequest CreateUpdateRequest() =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: StandaloneExtensionChildUpdateBody(),
            Headers: [],
            TraceId: new TraceId("no-profile-standalone-extension-child-update"),
            DocumentUuid: StandaloneExtensionChildDocumentUuid
        );

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

    private async Task<long> ReadDocumentIdAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", StandaloneExtensionChildDocumentUuid.Value)
        );

        return rows.Count == 1
            ? GetInt64(rows[0], "DocumentId")
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{StandaloneExtensionChildDocumentUuid.Value}', but found {rows.Count}."
            );
    }

    private async Task<ExtensionChildSchoolRow> ReadSchoolAsync(long documentId)
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
            ? new ExtensionChildSchoolRow(GetInt64(rows[0], "DocumentId"), GetInt64(rows[0], "SchoolId"))
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<ExtensionChildAddressRow>> ReadSchoolAddressesAsync(long documentId)
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

        return rows.Select(row => new ExtensionChildAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<ExtensionInterventionRow>> ReadInterventionsAsync(long documentId)
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

        return rows.Select(row => new ExtensionInterventionRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "InterventionCode")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<ExtensionInterventionVisitRow>> ReadInterventionVisitsAsync(
        long documentId
    )
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

        return rows.Select(row => new ExtensionInterventionVisitRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "VisitCode")
            ))
            .ToArray();
    }

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

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
