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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.NoProfileUpdateSemanticsScenarios;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Live-provider coverage for the create-side ownership verdict on SQL Server: a POST that resolves to a
/// create under <c>OwnershipBased</c> is denied when the client could not own the row it would write.
/// </summary>
/// <remarks>
/// <para>
/// The executor tests prove a denied create issues no data-modifying statement; these confirm it end to end.
/// Every denial counts <c>dms.Document</c> and the School root table before and after, and a denied create is
/// followed where it matters by a create that must still find nothing, so a leftover identity would show.
/// </para>
/// <para>
/// The verdict is decided in C#, so it must add nothing to the SQL either. The command-stream tests record a
/// denied create and a permitted create of the same request on the real session and require the denied stream
/// to be exactly the permitted one up to its first write, on each first-phase path.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Post_Create_With_Ownership_Authorization
{
    private const short CreatorToken = 42;
    private const short OtherToken = 7;

    private static readonly AuthorizationStrategyEvaluator[] _ownershipOnly = Evaluators(
        AuthorizationStrategyNameConstants.OwnershipBased
    );

    private static readonly AuthorizationStrategyEvaluator[] _noFurther = Evaluators(
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
    );

    private static readonly UpsertActionAuthorization _sharedOwnership =
        UpsertActionAuthorization.SamePolicyForCreateAndUpdate(_ownershipOnly);

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private RelationalWriteSessionCommandRecorder _recorder = null!;

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
        _recorder = _serviceProvider.GetRequiredService<RelationalWriteSessionCommandRecorder>();
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

    // ----- Denied creates ---------------------------------------------------

    /// <summary>
    /// A client with no creator token would stamp a row nobody can reach: auth.md 2.14, and nothing is written.
    /// </summary>
    [Test]
    public async Task It_denies_a_create_by_a_client_with_no_creator_token_and_writes_no_row()
    {
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));

        var result = await PostAsync(_sharedOwnership, creatorOwnershipTokenId: null, [CreatorToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));
    }

    /// <summary>
    /// A creator token outside the client's own list would stamp a row the client cannot reach: auth.md 2.13.
    /// </summary>
    [Test]
    public async Task It_denies_a_create_whose_creator_token_is_outside_the_client_tokens_and_writes_no_row()
    {
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));

        var result = await PostAsync(_sharedOwnership, CreatorToken, [OtherToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 0);
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));
    }

    /// <summary>
    /// The defensive cap fails a create too, attributed to the Create action. The list holds the creator token,
    /// so membership alone would have allowed it: the cap is checked first.
    /// </summary>
    [Test]
    public async Task It_fails_closed_for_a_create_at_the_ownership_token_cap_and_writes_no_row()
    {
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));

        var result = await PostAsync(
            _sharedOwnership,
            CreatorToken,
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit)
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure.TargetAction.Should().Be(UpsertTargetAction.Create);
        failure.Errors.Should().ContainSingle().Which.Should().Contain("2,000");
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));
    }

    // ----- Permitted creates ------------------------------------------------

    [Test]
    public async Task It_creates_and_stamps_for_a_client_holding_its_creator_token()
    {
        var result = await PostAsync(_sharedOwnership, CreatorToken, [OtherToken, CreatorToken]);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await CountRowsAsync()).Should().Be(new RowCounts(1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    /// <summary>
    /// One below the cap is an ordinary list: the create is decided by membership and succeeds.
    /// </summary>
    [Test]
    public async Task It_creates_and_stamps_with_a_token_list_one_below_the_cap()
    {
        var result = await PostAsync(
            _sharedOwnership,
            CreatorToken,
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit - 1)
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await CountRowsAsync()).Should().Be(new RowCounts(1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    // ----- Retries and target resolution ------------------------------------

    /// <summary>
    /// A denial leaves nothing behind, so a retry resolves to a create again and is denied again, and an owner's
    /// create afterwards is still a create rather than an update of something the denials left.
    /// </summary>
    [Test]
    public async Task It_denies_a_retried_create_again_and_leaves_nothing_for_a_later_create_to_find()
    {
        var first = await PostAsync(_sharedOwnership, creatorOwnershipTokenId: null, [CreatorToken]);
        var retry = await PostAsync(_sharedOwnership, creatorOwnershipTokenId: null, [CreatorToken]);

        AssertDenied(first, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
        AssertDenied(retry, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));

        var owner = await PostAsync(_sharedOwnership, CreatorToken, [CreatorToken]);

        owner.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await CountRowsAsync()).Should().Be(new RowCounts(1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    /// <summary>
    /// Under one shared policy, a read/modify-only client — no creator token, but holding the row's token —
    /// updates a record it owns. The same client's create would be denied, so only the resolved target can be
    /// selecting the stored-token check over the create verdict.
    /// </summary>
    [Test]
    public async Task It_updates_an_owned_record_for_a_client_without_a_creator_token()
    {
        var created = await PostAsync(_sharedOwnership, CreatorToken, [CreatorToken]);
        created.Should().BeOfType<UpsertResult.InsertSuccess>();

        var updated = await PostAsync(
            _sharedOwnership,
            creatorOwnershipTokenId: null,
            [CreatorToken],
            UpdateRequestBody()
        );

        updated.Should().BeOfType<UpsertResult.UpdateSuccess>();
        (await CountRowsAsync()).Should().Be(new RowCounts(1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    // ----- Policy splits ----------------------------------------------------

    /// <summary>
    /// Ownership on Create only: the create is denied, and once the record exists the same client updates it,
    /// because the Update policy carries no ownership check.
    /// </summary>
    [Test]
    public async Task It_denies_only_the_create_when_only_the_create_policy_checks_ownership()
    {
        var createChecksOwnership = new UpsertActionAuthorization(
            Permitted(_ownershipOnly),
            Permitted(_noFurther)
        );

        var refused = await PostAsync(createChecksOwnership, creatorOwnershipTokenId: null, []);

        AssertDenied(refused, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));

        var seeded = await PostAsync(
            UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
            CreatorToken,
            []
        );
        seeded.Should().BeOfType<UpsertResult.InsertSuccess>();

        var updated = await PostAsync(
            createChecksOwnership,
            creatorOwnershipTokenId: null,
            [],
            UpdateRequestBody()
        );

        updated.Should().BeOfType<UpsertResult.UpdateSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(CreatorToken);
    }

    /// <summary>
    /// Ownership on Update only: the create is not checked and stamps null, and the same client's next POST
    /// resolves to an update that the stored-token check denies as auth.md 2.14, leaving the row as it was.
    /// </summary>
    [Test]
    public async Task It_creates_unchecked_and_checks_the_stored_token_when_only_the_update_policy_checks_ownership()
    {
        var updateChecksOwnership = new UpsertActionAuthorization(
            Permitted(_noFurther),
            Permitted(_ownershipOnly)
        );

        var created = await PostAsync(updateChecksOwnership, creatorOwnershipTokenId: null, []);

        created.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await CountRowsAsync()).Should().Be(new RowCounts(1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().BeNull();

        var refused = await PostAsync(
            updateChecksOwnership,
            creatorOwnershipTokenId: null,
            [],
            UpdateRequestBody()
        );

        AssertDenied(refused, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
        (await CountRowsAsync()).Should().Be(new RowCounts(1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().BeNull();
    }

    // ----- Duplicate configuration ------------------------------------------

    /// <summary>
    /// <c>OwnershipBased</c> configured twice yields one verdict, attributed to its earliest position.
    /// </summary>
    [Test]
    public async Task It_reports_one_verdict_at_the_earliest_of_duplicate_ownership_strategies()
    {
        var duplicated = UpsertActionAuthorization.SamePolicyForCreateAndUpdate(
            Evaluators(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.OwnershipBased,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                AuthorizationStrategyNameConstants.OwnershipBased
            )
        );

        var result = await PostAsync(duplicated, CreatorToken, [OtherToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 1);
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));
    }

    /// <summary>
    /// Under split policies the index comes from the Create policy's own list, not the Update policy's.
    /// </summary>
    [Test]
    public async Task It_attributes_a_create_denial_to_the_create_policy_earliest_ownership_position()
    {
        var split = new UpsertActionAuthorization(
            Permitted(
                Evaluators(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                    AuthorizationStrategyNameConstants.OwnershipBased,
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                    AuthorizationStrategyNameConstants.OwnershipBased
                )
            ),
            Permitted(_ownershipOnly)
        );

        var result = await PostAsync(split, CreatorToken, [OtherToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, 1);
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));
    }

    // ----- The verdict adds no SQL ------------------------------------------

    /// <summary>
    /// The stored-token statement rides the capture's single composite command under its row guard; a present
    /// verdict leaves that command, and every parameter it binds, exactly as a permitted create sends it.
    /// </summary>
    [Test]
    public async Task It_adds_no_statement_or_parameter_to_the_single_composite_first_phase()
    {
        var (denied, permitted) = await RecordDeniedThenPermittedCreateAsync(
            _sharedOwnership,
            [CreatorToken]
        );

        AssertCaptureCarriesTheStoredTokenCheck(denied, expected: true);
        AssertDeniedStreamIsThePermittedFirstPhase(denied, permitted);
    }

    /// <summary>
    /// An Update the client is not permitted is a branch result owed right after capture, which sends the
    /// request down the ordered-segments path: the capture runs alone, and the stored checks behind it run
    /// only for an existing target. The verdict changes nothing there either.
    /// </summary>
    [Test]
    public async Task It_adds_no_statement_or_parameter_to_the_ordered_segments_first_phase()
    {
        var createOnlyWithOwnership = new UpsertActionAuthorization(
            Permitted(_ownershipOnly),
            UpsertActionPolicy.NotPermitted.Instance
        );

        var (denied, permitted) = await RecordDeniedThenPermittedCreateAsync(
            createOnlyWithOwnership,
            [CreatorToken]
        );

        AssertCaptureCarriesTheStoredTokenCheck(denied, expected: false);
        AssertDeniedStreamIsThePermittedFirstPhase(denied, permitted);
    }

    /// <summary>
    /// The largest list below the cap. SQL Server binds one scalar per token, so the composite command carries
    /// 1,999 ownership parameters beside the capture's — still inside the 2,098 budget for this request — and
    /// the verdict adds none of its own to the budget that decides that. The fallback when the budget is
    /// exceeded is pinned in the first-phase unit tests, where the budget can be set.
    /// </summary>
    [Test]
    public async Task It_adds_no_statement_or_parameter_to_the_first_phase_of_a_token_list_one_below_the_cap()
    {
        var (denied, permitted) = await RecordDeniedThenPermittedCreateAsync(
            _sharedOwnership,
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit - 1)
        );

        AssertCaptureCarriesTheStoredTokenCheck(denied, expected: true);
        AssertDeniedStreamIsThePermittedFirstPhase(denied, permitted);
    }

    /// <summary>
    /// Tells the two first-phase paths apart for a create: the single composite command co-batches the
    /// stored-token statement behind the capture, while the ordered-segments capture runs alone.
    /// </summary>
    private static void AssertCaptureCarriesTheStoredTokenCheck(
        IReadOnlyList<RecordedSessionCommand> denied,
        bool expected
    )
    {
        var capture = denied.Should().ContainSingle().Subject;
        capture
            .CommandText.Contains("[CreatedByOwnershipTokenId]", StringComparison.Ordinal)
            .Should()
            .Be(expected, capture.CommandText);
    }

    /// <summary>
    /// Records a create denied for its missing creator token, then the same request from a client that holds
    /// its creator token. The denial writes nothing, so both run against the same empty database.
    /// </summary>
    private async Task<(
        IReadOnlyList<RecordedSessionCommand> Denied,
        IReadOnlyList<RecordedSessionCommand> Permitted
    )> RecordDeniedThenPermittedCreateAsync(
        UpsertActionAuthorization actionAuthorization,
        IReadOnlyList<short> ownershipTokenIds
    )
    {
        _recorder.Reset();
        var deniedResult = await PostAsync(
            actionAuthorization,
            creatorOwnershipTokenId: null,
            ownershipTokenIds
        );
        AssertDenied(deniedResult, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized, 0);
        _recorder.ShouldHaveTransactionBoundary(1, 0, 1);
        RecordedSessionCommand[] denied = [.. _recorder.Commands];
        (await CountRowsAsync()).Should().Be(new RowCounts(0, 0));

        _recorder.Reset();
        var permittedResult = await PostAsync(actionAuthorization, CreatorToken, ownershipTokenIds);
        permittedResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _recorder.ShouldHaveTransactionBoundary(1, 1, 0);
        RecordedSessionCommand[] permitted = [.. _recorder.Commands];

        return (denied, permitted);
    }

    /// <summary>
    /// The denied stream is the permitted stream's prefix, statement text and bound parameters alike, and
    /// stops exactly where the permitted create starts writing: no insert and no collection-key reservation.
    /// </summary>
    private static void AssertDeniedStreamIsThePermittedFirstPhase(
        IReadOnlyList<RecordedSessionCommand> denied,
        IReadOnlyList<RecordedSessionCommand> permitted
    )
    {
        permitted.Should().HaveCountGreaterThan(denied.Count);
        permitted.Take(denied.Count).Should().BeEquivalentTo(denied, options => options.WithStrictOrdering());
        denied.Should().NotContain(static command => IsWrite(command.CommandText));
        IsWrite(permitted[denied.Count].CommandText)
            .Should()
            .BeTrue("the permitted create's next command is where it starts writing");
    }

    private static bool IsWrite(string commandText) =>
        commandText.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
        || commandText.Contains("CollectionItemIdSequence", StringComparison.Ordinal);

    // ----- Support ----------------------------------------------------------

    private static void AssertDenied(
        UpsertResult result,
        OwnershipAuthorizationFailureKind expectedKind,
        int expectedConfiguredIndex
    )
    {
        var failure = result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureOwnershipNotAuthorized>()
            .Subject.OwnershipFailure;
        failure.FailureKind.Should().Be(expectedKind);
        failure.ConfiguredStrategyIndex.Should().Be(expectedConfiguredIndex);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    private async Task<UpsertResult> PostAsync(
        UpsertActionAuthorization actionAuthorization,
        short? creatorOwnershipTokenId,
        IReadOnlyList<short> ownershipTokenIds,
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
                TraceId: new TraceId("mssql-create-ownership-post"),
                DocumentUuid: SchoolDocumentUuid
            )
            {
                AuthorizationContext = new RelationalAuthorizationContext(
                    [],
                    [],
                    creatorOwnershipTokenId,
                    ownershipTokenIds
                ),
                ActionAuthorization = actionAuthorization,
            }
        );
    }

    private sealed record RowCounts(int Documents, int Schools);

    private async Task<RowCounts> CountRowsAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                (SELECT COUNT(*) FROM [dms].[Document]) AS [DocumentCount],
                (SELECT COUNT(*) FROM [edfi].[School]) AS [SchoolCount];
            """
        );

        return new RowCounts(
            Convert.ToInt32(rows[0]["DocumentCount"], CultureInfo.InvariantCulture),
            Convert.ToInt32(rows[0]["SchoolCount"], CultureInfo.InvariantCulture)
        );
    }

    private async Task<short?> ReadStoredOwnershipTokenAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [CreatedByOwnershipTokenId]
            FROM [dms].[Document];
            """
        );

        rows.Should().ContainSingle();
        var value = rows[0]["CreatedByOwnershipTokenId"];
        return value is null or DBNull ? null : Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    private static short[] TokenRange(int count) =>
        [.. Enumerable.Range(1, count).Select(static tokenId => (short)tokenId)];

    private static AuthorizationStrategyEvaluator[] Evaluators(params string[] strategyNames) =>
        [
            .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                strategyName,
                [],
                FilterOperator.And
            )),
        ];

    private static UpsertActionPolicy.Permitted Permitted(AuthorizationStrategyEvaluator[] evaluators) =>
        new(evaluators);

    private IServiceScope CreateScopeForDatabase()
    {
        var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlRelationalCreateOwnershipAuthorization",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return scope;
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<RelationalWriteSessionCommandRecorder>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlBackendIntegrationTestServices();

        // Decorate the provider's own session factory so the recorder observes the real production session.
        services.AddScoped<IRelationalWriteSessionFactory>(
            serviceProvider => new RecordingRelationalWriteSessionFactory(
                ActivatorUtilities.CreateInstance<MssqlRelationalWriteSessionFactory>(serviceProvider),
                serviceProvider.GetRequiredService<RelationalWriteSessionCommandRecorder>()
            )
        );

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
