// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
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

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Live-provider coverage for descriptor GET-by-id under <c>OwnershipBased</c> on SQL Server, through the
/// repository and the production descriptor read handler.
/// </summary>
/// <remarks>
/// The descriptor is seeded directly and its <c>dms.Document.CreatedByOwnershipTokenId</c> then set with SQL, so
/// each arm of the stored-stamp check — owned, foreign, never assigned — is reached by choosing the stamp and
/// the tokens the reader holds. Only a live engine proves the compiled ownership statement parses against a
/// descriptor's document row and that its AUTH1 abort decodes back into the response.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Descriptor_Get_By_Id_With_Ownership_Authorization
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const short OwnedToken = 42;
    private const short OtherToken = 7;

    private static readonly QualifiedResourceName _schoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly DocumentUuid _documentUuid = new(
        Guid.Parse("31000000-0000-0000-0000-000000000101")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;

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
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
        _serviceProvider = CreateServiceProvider();
        _resourceInfo = DescriptorReadIntegrationTestSupport.CreateResourceInfo(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "SchoolTypeDescriptor"
        );
    }

    [SetUp]
    public async Task Setup() => await _database.ResetAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_serves_a_descriptor_whose_stored_token_the_reader_holds()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync([OtherToken, OwnedToken]);

        result.Should().BeOfType<GetResult.GetSuccess>().Which.DocumentUuid.Should().Be(_documentUuid);
    }

    /// <summary>
    /// auth.md 2.13. The row exists and its owner can read it, so only the membership predicate is denying.
    /// </summary>
    [Test]
    public async Task It_denies_a_descriptor_whose_stored_token_the_reader_does_not_hold()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync([OtherToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 0);
    }

    /// <summary>
    /// A reader with no tokens still runs the check, so the constant-false membership predicate must be valid
    /// SQL on this engine. The row carries a token, so this is a mismatch.
    /// </summary>
    [Test]
    public async Task It_denies_a_descriptor_to_a_reader_holding_no_tokens()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync([]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 0);
    }

    /// <summary>
    /// auth.md 2.14: a descriptor created before stamping, or by a client with no creator token, is
    /// unreachable through ownership. Intentional, matching ODS.
    /// </summary>
    [Test]
    public async Task It_denies_a_descriptor_whose_stored_token_was_never_assigned()
    {
        await SeedDescriptorAsync(storedToken: null);

        var result = await GetByIdAsync([OwnedToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
    }

    [Test]
    public async Task It_reports_not_exists_for_a_missing_descriptor_rather_than_an_ownership_denial()
    {
        var result = await GetByIdAsync([OtherToken]);

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
    }

    /// <summary>
    /// The configured position travels through the emitted payload and back.
    /// </summary>
    [Test]
    public async Task It_reports_the_configured_strategy_index_a_descriptor_denial_came_from()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync(
            [OtherToken],
            [
                Strategy(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired),
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased),
            ]
        );

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 1);
    }

    /// <summary>
    /// Ownership configured first still yields to a namespace denial: both would deny, and the namespace
    /// check precedes ownership among the AND strategies.
    /// </summary>
    [Test]
    public async Task It_reports_a_namespace_denial_ahead_of_an_ownership_denial()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync(
            [OtherToken],
            [
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased),
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://other.example/"]
        );

        result
            .Should()
            .BeOfType<GetResult.GetFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
    }

    /// <summary>
    /// With the namespace authorized, the ownership denial is the one reported.
    /// </summary>
    [Test]
    public async Task It_reports_the_ownership_denial_once_the_namespace_check_passes()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync(
            [OtherToken],
            [
                Strategy(AuthorizationStrategyNameConstants.OwnershipBased),
                Strategy(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 0);
    }

    /// <summary>
    /// The provider-independent defensive limit, reported before any statement reaches the engine.
    /// </summary>
    [Test]
    public async Task It_fails_closed_for_a_descriptor_get_by_id_at_the_ownership_token_cap()
    {
        await SeedDescriptorAsync(OwnedToken);

        var result = await GetByIdAsync([
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

    private static void AssertDenied(
        GetResult result,
        OwnershipAuthorizationFailureKind expectedKind,
        int expectedConfiguredIndex
    )
    {
        var failure = result
            .Should()
            .BeOfType<GetResult.GetFailureOwnershipNotAuthorized>()
            .Subject.OwnershipFailure;
        failure.FailureKind.Should().Be(expectedKind);
        failure.ConfiguredStrategyIndex.Should().Be(expectedConfiguredIndex);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// Seeds the descriptor, then sets its stamp directly. Product code never updates the stamp, so this
    /// fabricates exactly the state a create would leave, including the legacy null.
    /// </summary>
    private async Task SeedDescriptorAsync(short? storedToken)
    {
        var documentId = await MssqlDescriptorReadTestSupport.SeedDescriptorAsync(
            _database,
            _mappingSet,
            _schoolTypeDescriptorResource,
            new DescriptorReadSeed(
                DocumentUuid: _documentUuid,
                Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
                CodeValue: "Alternative",
                ShortDescription: "Alternative"
            )
        );

        var updated = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Document]
            SET [CreatedByOwnershipTokenId] = @storedToken
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@storedToken", SqlDbType.SmallInt)
            {
                Value = (object?)storedToken ?? DBNull.Value,
            },
            new SqlParameter("@documentId", documentId)
        );
        updated.Should().Be(1);
    }

    private async Task<GetResult> GetByIdAsync(
        IReadOnlyList<short> ownershipTokenIds,
        AuthorizationStrategyEvaluator[]? strategies = null,
        IReadOnlyList<string>? namespacePrefixes = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlDescriptorOwnershipAuthorization",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var request = DescriptorReadIntegrationTestSupport.CreateGetRequest(
            _documentUuid,
            _resourceInfo,
            _mappingSet,
            new TraceId("mssql-descriptor-ownership-get"),
            authorizationStrategyEvaluators: strategies
                ?? [Strategy(AuthorizationStrategyNameConstants.OwnershipBased)]
        ) with
        {
            AuthorizationContext = new RelationalAuthorizationContext(
                [],
                namespacePrefixes ?? [],
                creatorOwnershipTokenId: null,
                ownershipTokenIds
            ),
        };

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    private static AuthorizationStrategyEvaluator Strategy(string strategyName) =>
        new(strategyName, [], FilterOperator.And);

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

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
