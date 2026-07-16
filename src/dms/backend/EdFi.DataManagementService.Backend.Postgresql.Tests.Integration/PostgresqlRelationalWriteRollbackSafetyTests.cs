// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
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

file sealed class FailOnSchoolAddressInsertPostgresqlRelationalWriteSessionFactory(
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

            return new FailOnSchoolAddressInsertRelationalWriteSession(
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

file sealed class FailOnSchoolAddressInsertRelationalWriteSession(
    IRelationalWriteSession innerSession,
    RollbackSafetyCommandRecorder commandRecorder
) : IRelationalWriteSession
{
    private const string SchoolAddressInsertSql = "INSERT INTO \"edfi\".\"SchoolAddress\"";

    private readonly IRelationalWriteSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));
    private readonly RollbackSafetyCommandRecorder _commandRecorder =
        commandRecorder ?? throw new ArgumentNullException(nameof(commandRecorder));

    public DbConnection Connection => _innerSession.Connection;

    public DbTransaction Transaction => _innerSession.Transaction;

    public DbCommand CreateCommand(RelationalCommand command)
    {
        _commandRecorder.Record(command);

        if (command.CommandText.Contains(SchoolAddressInsertSql, StringComparison.Ordinal))
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
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

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
            FailOnSchoolAddressInsertPostgresqlRelationalWriteSessionFactory
        >();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static JsonNode CreateCreateRequestBody() =>
        new JsonObject
        {
            ["schoolId"] = 255901,
            ["shortName"] = "ROLLBACK",
            ["addresses"] = new JsonArray
            {
                new JsonObject { ["city"] = "Austin" },
                new JsonObject { ["city"] = "Dallas" },
            },
        };

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        JsonNode edfiDoc,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: schoolIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: mappingSet,
            EdfiDoc: edfiDoc,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );
    }

    public static Task<long> ReadDocumentCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."Document";
            """
        );

    public static Task<long> ReadSchoolCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "edfi"."School";
            """
        );

    public static Task<long> ReadSchoolAddressCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "edfi"."SchoolAddress";
            """
        );
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
    private long _documentCount;
    private long _schoolCount;
    private long _schoolAddressCount;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            RollbackSafetyIntegrationTestSupport.FixtureRelativePath
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
            ?? throw new AssertionException("Expected the injected SchoolAddress failure to be thrown.");

        _documentCount = await RollbackSafetyIntegrationTestSupport.ReadDocumentCountAsync(_database);
        _schoolCount = await RollbackSafetyIntegrationTestSupport.ReadSchoolCountAsync(_database);
        _schoolAddressCount = await RollbackSafetyIntegrationTestSupport.ReadSchoolAddressCountAsync(
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
        NoProfileAtomicRollbackAssertions.AssertNoPartialRelationalStateAfterRollback(
            _documentCount,
            _schoolCount,
            _schoolAddressCount
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
            RollbackSafetyIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                RollbackSafetyIntegrationTestSupport.CreateCreateRequestBody(),
                SchoolDocumentUuid,
                "pg-rollback-safety"
            )
        );
    }

    // Translates the recorded PostgreSQL command text into the provider-neutral ordered write steps
    // consumed by the shared rollback assertion. Provider dialect SQL stays here, not in the contract.
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
