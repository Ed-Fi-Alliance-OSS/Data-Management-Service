// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
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
/// Live-provider coverage for <c>dms.Document.CreatedByOwnershipTokenId</c> stamping on PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests assert the emitted SQL and parameter binding; only a live provider can prove the column
/// actually round-trips. Two things specifically need a real engine: that a nullable <c>smallint</c> parameter
/// declared as <see cref="DbType.Int16"/> binds for both a value and a null, and that an update genuinely
/// leaves the stored token alone rather than merely omitting it from a statement the unit tests inspected.
/// </para>
/// <para>
/// Stamping only — this suite asserts nothing about ownership enforcement, which is not yet wired.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_With_Ownership_Stamping
{
    private const short CreatorToken = 42;
    private const short OtherToken = 7;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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
    public async Task It_stamps_the_creator_ownership_token_on_a_create()
    {
        var createResult = await ExecuteCreateAsync(CreatorToken);

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    /// <summary>
    /// A client with no creator token stamps null. This is the case the declared parameter type exists for: a
    /// null reaches the driver as <c>DBNull</c>, which carries no type of its own.
    /// </summary>
    [Test]
    public async Task It_stamps_null_when_the_client_has_no_creator_token()
    {
        var createResult = await ExecuteCreateAsync(creatorOwnershipTokenId: null);

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().BeNull();
    }

    [Test]
    public async Task It_leaves_the_stored_token_unchanged_on_a_put()
    {
        await ExecuteCreateAsync(CreatorToken);

        var updateResult = await ExecuteUpdateAsync(OtherToken);

        updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    /// <summary>
    /// A POST that resolves to an upsert-as-update must also leave the stored token alone, even though its
    /// request carries a creator token that a create would have stamped.
    /// </summary>
    [Test]
    public async Task It_leaves_the_stored_token_unchanged_on_a_post_as_update()
    {
        await ExecuteCreateAsync(CreatorToken);

        var upsertResult = await ExecuteCreateAsync(OtherToken, UpdateRequestBody());

        upsertResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    /// <summary>
    /// A document stamped null stays null through an update by a client that does have a creator token — an
    /// unstamped document is permanently unreachable through ownership authorization, and an update must not
    /// quietly repair it.
    /// </summary>
    [Test]
    public async Task It_leaves_a_null_stored_token_null_on_a_put()
    {
        await ExecuteCreateAsync(creatorOwnershipTokenId: null);

        await ExecuteUpdateAsync(CreatorToken);

        (await ReadStoredOwnershipTokenAsync()).Should().BeNull();
    }

    private async Task<UpsertResult> ExecuteCreateAsync(
        short? creatorOwnershipTokenId,
        JsonNode? requestBody = null
    )
    {
        using var scope = CreateScopeForDatabase();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            new UpsertRequest(
                ResourceInfo: SchoolResourceInfo,
                DocumentInfo: CreateSchoolDocumentInfo(),
                MappingSet: _mappingSet,
                EdfiDoc: requestBody ?? CreateRequestBody(),
                Headers: [],
                TraceId: new TraceId("pg-ownership-stamping-post"),
                DocumentUuid: SchoolDocumentUuid
            )
            {
                AuthorizationContext = CreateAuthorizationContext(creatorOwnershipTokenId),
            }
        );
    }

    private async Task<UpdateResult> ExecuteUpdateAsync(short? creatorOwnershipTokenId)
    {
        using var scope = CreateScopeForDatabase();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            new UpdateRequest(
                ResourceInfo: SchoolResourceInfo,
                DocumentInfo: CreateSchoolDocumentInfo(),
                MappingSet: _mappingSet,
                EdfiDoc: UpdateRequestBody(),
                Headers: [],
                TraceId: new TraceId("pg-ownership-stamping-put"),
                DocumentUuid: SchoolDocumentUuid
            )
            {
                AuthorizationContext = CreateAuthorizationContext(creatorOwnershipTokenId),
            }
        );
    }

    private static RelationalAuthorizationContext CreateAuthorizationContext(
        short? creatorOwnershipTokenId
    ) => new([], [], creatorOwnershipTokenId, []);

    private IServiceScope CreateScopeForDatabase()
    {
        var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalOwnershipStamping",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return scope;
    }

    private async Task<short?> ReadStoredOwnershipTokenAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CreatedByOwnershipTokenId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", SchoolDocumentUuid.Value)
        );

        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document row for '{SchoolDocumentUuid.Value}', but found {rows.Count}."
            );
        }

        var value = rows[0]["CreatedByOwnershipTokenId"];
        return value is null or DBNull ? null : Convert.ToInt16(value, CultureInfo.InvariantCulture);
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
        services.AddPostgresqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
