// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.NoProfileUpdateSemanticsScenarios;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server adapter for the shared <c>NoProfileChangedPutOmissionSemantics</c> changed-PUT
/// omission behaviors. This suite owns only the SQL Server provisioning, resolver registration,
/// no-profile production-boundary invocation, and SQL readback; the request bodies, persisted-state
/// snapshot shapes, and behavioral assertions come from <see cref="NoProfileUpdateSemanticsScenarios"/>
/// in Backend.Tests.Common.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture
{
    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpdateResult _updateResult = null!;
    private DocumentRow _documentBeforeUpdate = null!;
    private DocumentRow _documentAfterUpdate = null!;
    private SchoolRow _schoolAfterUpdate = null!;
    private IReadOnlyList<SchoolAddressRow> _addressesBeforeUpdate = null!;
    private IReadOnlyList<SchoolAddressRow> _addressesAfterUpdate = null!;
    private IReadOnlyList<SchoolExtensionAddressRow> _extensionAddressesBeforeUpdate = null!;
    private IReadOnlyList<SchoolExtensionAddressRow> _extensionAddressesAfterUpdate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();

        await ExecuteCreateAsync();

        _documentBeforeUpdate = await ReadDocumentAsync();
        _addressesBeforeUpdate = await ReadSchoolAddressesAsync(_documentBeforeUpdate.DocumentId);
        _extensionAddressesBeforeUpdate = await ReadSchoolExtensionAddressesAsync(
            _documentBeforeUpdate.DocumentId
        );

        _updateResult = await ExecuteUpdateAsync();

        _documentAfterUpdate = await ReadDocumentAsync();
        _schoolAfterUpdate = await ReadSchoolAsync(_documentAfterUpdate.DocumentId);
        _addressesAfterUpdate = await ReadSchoolAddressesAsync(_documentAfterUpdate.DocumentId);
        _extensionAddressesAfterUpdate = await ReadSchoolExtensionAddressesAsync(
            _documentAfterUpdate.DocumentId
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
    public void It_returns_update_success_and_bumps_content_version_for_the_put_flow() =>
        AssertUpdateSuccessAndContentVersionBump(
            _updateResult,
            _mappingSet,
            _documentBeforeUpdate,
            _documentAfterUpdate
        );

    [Test]
    public void It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value() =>
        AssertClearedOmittedInlinedColumn(_documentAfterUpdate, _schoolAfterUpdate);

    [Test]
    public void It_deletes_omitted_collection_aligned_extension_scope_rows_without_deleting_base_rows() =>
        AssertDeletedOmittedAlignedExtensionScope(
            _documentAfterUpdate,
            _addressesBeforeUpdate,
            _extensionAddressesBeforeUpdate,
            _addressesAfterUpdate,
            _extensionAddressesAfterUpdate
        );

    private async Task ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalWriteUpdateSemantics",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var createResult = await repository.UpsertDocument(CreateUpsertRequest());
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
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
                    Name: "MssqlRelationalWriteUpdateSemantics",
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
            EdfiDoc: CreateRequestBody(),
            Headers: [],
            TraceId: new TraceId("no-profile-update-semantics-create"),
            DocumentUuid: SchoolDocumentUuid
        );

    private UpdateRequest CreateUpdateRequest() =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: _mappingSet,
            EdfiDoc: UpdateRequestBody(),
            Headers: [],
            TraceId: new TraceId("no-profile-update-semantics-update"),
            DocumentUuid: SchoolDocumentUuid
        );

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private async Task<DocumentRow> ReadDocumentAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", SchoolDocumentUuid.Value)
        );

        return rows.Count == 1
            ? new DocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{SchoolDocumentUuid.Value}', but found {rows.Count}."
            );
    }

    private async Task<SchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [DocumentId], [SchoolId], [ShortName]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new SchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<SchoolAddressRow>> ReadSchoolAddressesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [School_DocumentId], [Ordinal], [City]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new SchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<SchoolExtensionAddressRow>> ReadSchoolExtensionAddressesAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [BaseCollectionItemId], [School_DocumentId], [Zone]
            FROM [sample].[SchoolExtensionAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [BaseCollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new SchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
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

    private static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    private static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
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
