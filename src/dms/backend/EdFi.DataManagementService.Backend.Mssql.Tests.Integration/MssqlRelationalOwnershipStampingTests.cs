// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.NoProfileUpdateSemanticsScenarios;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Live-provider coverage for <c>dms.Document.CreatedByOwnershipTokenId</c> stamping on SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests assert the emitted SQL and parameter binding; only a live provider can prove the column
/// actually round-trips. Two things specifically need a real engine: that a nullable <c>smallint</c> parameter
/// declared as <see cref="DbType.Int16"/> binds for both a value and a null, and that an update genuinely
/// leaves the stored token alone rather than merely omitting it from a statement the unit tests inspected.
/// </para>
/// <para>
/// Stamping first, then enforcement: the same seeded row proves both, because what a create stamps is
/// exactly what a later GET-by-id has to authorize against.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Write_With_Ownership_Stamping
{
    private const short CreatorToken = 42;
    private const short OtherToken = 7;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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

    // ----- Enforcement -----------------------------------------------------
    // These execute the compiled ownership SQL against the real engine. Only a live provider can prove the
    // SELECT CASE parses, the membership predicate binds smallint to smallint, and the AUTH1 abort device
    // raises an error the dispatcher and mapper decode back into a response. The stamping tests above put a
    // known token on the row, so each arm is reached by choosing which tokens the reader holds.

    /// <summary>
    /// The stored token is one of the reader's, so the read is authorized and the document is served.
    /// </summary>
    [Test]
    public async Task It_authorizes_a_get_by_id_whose_stored_token_the_reader_holds()
    {
        await ExecuteCreateAsync(CreatorToken);

        var result = await ExecuteOwnershipGetByIdAsync([OtherToken, CreatorToken]);

        result.Should().BeOfType<GetResult.GetSuccess>();
    }

    /// <summary>
    /// The stored token is not one of the reader's: auth.md 2.13. The row exists and is readable by its own
    /// owner, so only the membership predicate can be denying it.
    /// </summary>
    [Test]
    public async Task It_denies_a_get_by_id_whose_stored_token_the_reader_does_not_hold()
    {
        await ExecuteCreateAsync(CreatorToken);

        var result = await ExecuteOwnershipGetByIdAsync([OtherToken]);

        result
            .Should()
            .BeOfType<GetResult.GetFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
    }

    /// <summary>
    /// A reader holding no tokens at all still runs the check rather than being short-circuited, so the
    /// constant-false membership predicate has to be valid SQL on this engine. The row carries a token, so
    /// this is a mismatch rather than the uninitialized case.
    /// </summary>
    [Test]
    public async Task It_denies_a_get_by_id_for_a_reader_holding_no_tokens()
    {
        await ExecuteCreateAsync(CreatorToken);

        var result = await ExecuteOwnershipGetByIdAsync([]);

        result
            .Should()
            .BeOfType<GetResult.GetFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
    }

    /// <summary>
    /// The stored token is null: auth.md 2.14, a different response type from 2.13 because the document can
    /// never be reached by any client rather than merely not by this one. Reached by creating through a
    /// client that has no creator token, which is exactly how such a row arises in practice.
    /// </summary>
    [Test]
    public async Task It_denies_a_get_by_id_whose_stored_token_was_never_assigned()
    {
        await ExecuteCreateAsync(creatorOwnershipTokenId: null);

        var result = await ExecuteOwnershipGetByIdAsync([CreatorToken]);

        result
            .Should()
            .BeOfType<GetResult.GetFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
    }

    /// <summary>
    /// The configured position travels through the emitted payload and back, so a denial is attributed to
    /// the position OwnershipBased actually holds rather than to a normalized zero.
    /// </summary>
    [Test]
    public async Task It_reports_the_configured_strategy_index_a_denial_came_from()
    {
        await ExecuteCreateAsync(CreatorToken);

        var result = await ExecuteOwnershipGetByIdAsync(
            [OtherToken],
            strategyNames:
            [
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.OwnershipBased,
            ]
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureOwnershipNotAuthorized>().Subject;
        failure.OwnershipFailure.ConfiguredStrategyIndex.Should().Be(1);
        failure.OwnershipFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// The provider-independent defensive limit, reported before any statement is emitted. Proves the cap
    /// terminal reaches the response on a live engine rather than the over-limit parameter list reaching the
    /// SQL boundary.
    /// </summary>
    [Test]
    public async Task It_fails_closed_for_a_get_by_id_with_an_over_cap_ownership_token_list()
    {
        await ExecuteCreateAsync(CreatorToken);

        var result = await ExecuteOwnershipGetByIdAsync([
            .. Enumerable
                .Range(1, OwnershipTokenLimitExceededException.OwnershipTokenLimit)
                .Select(static tokenId => (short)tokenId),
        ]);

        result
            .Should()
            .BeOfType<GetResult.GetFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2,000");
    }

    private async Task<GetResult> ExecuteOwnershipGetByIdAsync(
        IReadOnlyList<short> ownershipTokenIds,
        IReadOnlyList<string>? strategyNames = null
    )
    {
        using var scope = CreateScopeForDatabase();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.GetDocumentById(
            new IntegrationRelationalGetRequest(
                DocumentUuid: SchoolDocumentUuid,
                ResourceInfo: SchoolResourceInfo,
                MappingSet: _mappingSet,
                AuthorizationStrategyEvaluators:
                [
                    .. (strategyNames ?? [AuthorizationStrategyNameConstants.OwnershipBased]).Select(
                        static strategyName => new AuthorizationStrategyEvaluator(
                            strategyName,
                            [],
                            FilterOperator.And
                        )
                    ),
                ],
                TraceId: new TraceId("mssql-ownership-enforcement-get")
            )
            {
                AuthorizationContext = new RelationalAuthorizationContext(
                    [],
                    [],
                    creatorOwnershipTokenId: null,
                    ownershipTokenIds
                ),
            }
        );
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
                TraceId: new TraceId("mssql-ownership-stamping-post"),
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
                TraceId: new TraceId("mssql-ownership-stamping-put"),
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
                    Name: "MssqlRelationalOwnershipStamping",
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
            SELECT [CreatedByOwnershipTokenId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", SchoolDocumentUuid.Value)
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
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
