// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed class RollbackSafetyCommandRecorder
{
    private readonly List<string> _commandTexts = [];

    public IReadOnlyList<string> CommandTexts => _commandTexts;

    public void Record(RelationalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commandTexts.Add(command.CommandText);
    }
}

file sealed class FailOnAlignedExtensionAddressInsertPostgresqlRelationalWriteSessionFactory(
    NpgsqlDataSourceProvider dataSourceProvider,
    IOptions<DatabaseOptions> databaseOptions,
    RollbackSafetyCommandRecorder commandRecorder
) : IRelationalWriteSessionFactory
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));
    private readonly IsolationLevel _isolationLevel =
        databaseOptions?.Value.IsolationLevel ?? throw new ArgumentNullException(nameof(databaseOptions));
    private readonly RollbackSafetyCommandRecorder _commandRecorder =
        commandRecorder ?? throw new ArgumentNullException(nameof(commandRecorder));

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSourceProvider
            .DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var transaction = await connection
                .BeginTransactionAsync(_isolationLevel, cancellationToken)
                .ConfigureAwait(false);

            return new FailOnAlignedExtensionAddressInsertRelationalWriteSession(
                new RelationalWriteSession(connection, transaction),
                _commandRecorder
            );
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

file sealed class FailOnAlignedExtensionAddressInsertRelationalWriteSession(
    IRelationalWriteSession innerSession,
    RollbackSafetyCommandRecorder commandRecorder
) : IRelationalWriteSession
{
    // The aligned extension address rows (keyed to the base collection ids) are the write plan's
    // final command, so failing here proves the document, root, root extension, extension-child,
    // base collection, grandchild, identity, and tracking work already performed inside the
    // transaction all roll back. The closing quote keeps SchoolExtensionAddressSponsorReference
    // commands from matching.
    private const string AlignedExtensionAddressInsertSql =
        "INSERT INTO \"sample\".\"SchoolExtensionAddress\"";

    private readonly IRelationalWriteSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));
    private readonly RollbackSafetyCommandRecorder _commandRecorder =
        commandRecorder ?? throw new ArgumentNullException(nameof(commandRecorder));

    public DbConnection Connection => _innerSession.Connection;

    public DbTransaction Transaction => _innerSession.Transaction;

    public DbCommand CreateCommand(RelationalCommand command)
    {
        _commandRecorder.Record(command);

        if (command.CommandText.Contains(AlignedExtensionAddressInsertSql, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(NoProfileAtomicRollbackAssertions.InjectedFailureMessage);
        }

        return _innerSession.CreateCommand(command);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _innerSession.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _innerSession.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _innerSession.DisposeAsync();
}

file static class RollbackSafetyIntegrationTestSupport
{
    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddSingleton<RollbackSafetyCommandRecorder>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddScoped<
            IRelationalWriteSessionFactory,
            FailOnAlignedExtensionAddressInsertPostgresqlRelationalWriteSessionFactory
        >();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    /// <summary>
    /// The failing request is the shared full-surface create (root scalar, nested address/period
    /// collections, root extension, collection-aligned extension addresses, and extension-child
    /// intervention/visit collections) so the injected failure at the plan's final aligned-extension
    /// write lands after every earlier surface has been written inside the transaction.
    /// </summary>
    public static UpsertRequest CreateFullSurfaceCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: NoProfileCreateBaselineScenarios.SchoolResourceInfo,
            DocumentInfo: NoProfileCreateBaselineScenarios.CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: NoProfileCreateBaselineScenarios.CreateRequestBody(),
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

    public static async Task<NoProfileAtomicRollbackAssertions.FullSurfaceRollbackSnapshot> ReadFullSurfaceSnapshotAsync(
        PostgresqlGeneratedDdlTestDatabase database
    ) =>
        new(
            DocumentCount: await CountRowsAsync(database, "\"dms\".\"Document\""),
            ReferentialIdentityCount: await CountRowsAsync(database, "\"dms\".\"ReferentialIdentity\""),
            SchoolCount: await CountRowsAsync(database, "\"edfi\".\"School\""),
            SchoolAddressCount: await CountRowsAsync(database, "\"edfi\".\"SchoolAddress\""),
            SchoolAddressPeriodCount: await CountRowsAsync(database, "\"edfi\".\"SchoolAddressPeriod\""),
            SchoolExtensionCount: await CountRowsAsync(database, "\"sample\".\"SchoolExtension\""),
            SchoolExtensionAddressCount: await CountRowsAsync(
                database,
                "\"sample\".\"SchoolExtensionAddress\""
            ),
            SchoolExtensionInterventionCount: await CountRowsAsync(
                database,
                "\"sample\".\"SchoolExtensionIntervention\""
            ),
            SchoolExtensionInterventionVisitCount: await CountRowsAsync(
                database,
                "\"sample\".\"SchoolExtensionInterventionVisit\""
            ),
            SchoolTrackedChangeCount: await CountRowsAsync(database, "\"tracked_changes_edfi\".\"School\"")
        );

    private static Task<long> CountRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string qualifiedTableName
    ) => database.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {qualifiedTableName};");
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000002")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private RollbackSafetyCommandRecorder _commandRecorder = null!;
    private Exception _exception = null!;
    private NoProfileAtomicRollbackAssertions.FullSurfaceRollbackSnapshot _snapshotBeforeCreateAttempt =
        null!;
    private NoProfileAtomicRollbackAssertions.FullSurfaceRollbackSnapshot _snapshotAfterRollback = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            NoProfileCreateBaselineScenarios.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = RollbackSafetyIntegrationTestSupport.CreateServiceProvider();
        _commandRecorder = _serviceProvider.GetRequiredService<RollbackSafetyCommandRecorder>();

        _snapshotBeforeCreateAttempt =
            await RollbackSafetyIntegrationTestSupport.ReadFullSurfaceSnapshotAsync(_database);

        Exception? capturedException = null;

        try
        {
            await ExecuteCreateAsync();
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        _exception =
            capturedException
            ?? throw new AssertionException(
                "Expected the injected SchoolExtensionAddress failure to be thrown."
            );

        _snapshotAfterRollback = await RollbackSafetyIntegrationTestSupport.ReadFullSurfaceSnapshotAsync(
            _database
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
    public void It_surfaces_the_injected_failure_only_after_the_early_write_commands_are_attempted() =>
        NoProfileAtomicRollbackAssertions.AssertInjectedFailureAfterOrderedEarlyWrites(
            _exception,
            RecordedWriteSteps()
        );

    [Test]
    public void It_leaves_no_partial_relational_state_after_the_transaction_rolls_back() =>
        NoProfileAtomicRollbackAssertions.AssertFullSurfaceRollbackToPreState(
            _snapshotBeforeCreateAttempt,
            _snapshotAfterRollback
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
                    Name: "PostgresqlRelationalWriteRollbackSafety",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        await repository.UpsertDocument(
            RollbackSafetyIntegrationTestSupport.CreateFullSurfaceCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-rollback-safety"
            )
        );
    }

    // Translates the recorded PostgreSQL command text into the provider-neutral ordered write steps
    // consumed by the shared rollback assertion. Provider dialect SQL stays here, not in the contract.
    // Snippets keep the closing quote after the table name so a table name that prefixes another
    // (School/SchoolAddress, SchoolExtension/SchoolExtensionAddress) cannot match its extensions.
    private static readonly (
        string Snippet,
        NoProfileAtomicRollbackAssertions.RelationalWriteStep Step
    )[] WriteStepSnippets =
    [
        ("INSERT INTO dms.\"Document\"", NoProfileAtomicRollbackAssertions.RelationalWriteStep.Document),
        ("INSERT INTO \"edfi\".\"School\"", NoProfileAtomicRollbackAssertions.RelationalWriteStep.School),
        (
            "INSERT INTO \"edfi\".\"SchoolAddress\"",
            NoProfileAtomicRollbackAssertions.RelationalWriteStep.SchoolAddress
        ),
        (
            "INSERT INTO \"edfi\".\"SchoolAddressPeriod\"",
            NoProfileAtomicRollbackAssertions.RelationalWriteStep.SchoolAddressPeriod
        ),
        (
            "INSERT INTO \"sample\".\"SchoolExtension\"",
            NoProfileAtomicRollbackAssertions.RelationalWriteStep.SchoolExtension
        ),
        (
            "INSERT INTO \"sample\".\"SchoolExtensionAddress\"",
            NoProfileAtomicRollbackAssertions.RelationalWriteStep.SchoolExtensionAddress
        ),
        (
            "INSERT INTO \"sample\".\"SchoolExtensionIntervention\"",
            NoProfileAtomicRollbackAssertions.RelationalWriteStep.SchoolExtensionIntervention
        ),
        (
            "INSERT INTO \"sample\".\"SchoolExtensionInterventionVisit\"",
            NoProfileAtomicRollbackAssertions.RelationalWriteStep.SchoolExtensionInterventionVisit
        ),
    ];

    private IReadOnlyList<NoProfileAtomicRollbackAssertions.RelationalWriteStep> RecordedWriteSteps() =>
        _commandRecorder
            .CommandTexts.SelectMany(commandText =>
                WriteStepSnippets
                    .Where(entry => commandText.Contains(entry.Snippet, StringComparison.Ordinal))
                    .Select(entry => entry.Step)
            )
            .ToArray();
}
