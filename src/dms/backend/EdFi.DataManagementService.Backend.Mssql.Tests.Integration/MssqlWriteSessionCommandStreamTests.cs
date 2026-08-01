// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server mirror of the PostgreSQL write-session command-stream characterization. Same shared
/// expectations, same scenario bodies; only the dialect classification differs.
/// </summary>
file static class WriteSessionCommandStreamTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public static readonly ResourceInfo SchoolResourceInfo = new(
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
        services.AddSingleton<RelationalWriteSessionCommandRecorder>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        // Decorate the provider's own session factory so the recorder observes the real production
        // session, including BEGIN/COMMIT, rather than a substitute.
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

    public static JsonNode CreateRequestBody(int addressCount) =>
        NoProfileMultiBatchCollectionScenarios.CreateCollectionRequestBody(addressCount);

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

    public static UpsertRequest CreateUpsertRequest(
        MappingSet mappingSet,
        JsonNode edfiDoc,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: edfiDoc,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        JsonNode edfiDoc,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: edfiDoc,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

    public static UpsertRequest CreateBulkReferenceUpsertRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        int referenceCount
    )
    {
        var documentInfo = CreateSchoolDocumentInfo();
        var programResourceInfo = new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("Program"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false
        );
        var documentReferences = Enumerable
            .Range(0, referenceCount)
            .Select(index =>
            {
                var identity = new DocumentIdentity([
                    new DocumentIdentityElement(new JsonPath("$.programName"), $"missing-{index}"),
                ]);

                return new DocumentReference(
                    programResourceInfo,
                    identity,
                    ReferentialIdCalculator.ReferentialIdFrom(programResourceInfo, identity),
                    new JsonPath(
                        $"$._ext.sample.addresses[0]._ext.sample.sponsorReferences[{index}].programReference"
                    )
                );
            })
            .ToArray();

        return new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo with
            {
                DocumentReferences = documentReferences,
            },
            MappingSet: mappingSet,
            EdfiDoc: CreateRequestBody(0),
            Headers: [],
            TraceId: new TraceId("mssql-write-session-tvp-reference-fallback"),
            DocumentUuid: documentUuid
        );
    }

    /// <summary>
    /// Classifies recorded SQL Server command text into the provider-neutral summary the shared
    /// contract asserts over. Dialect text stays in this adapter, never in Tests.Common.
    /// </summary>
    public static WriteSessionCommandStreamSummary Summarize(
        RelationalWriteSessionCommandRecorder recorder
    ) =>
        new(
            TotalCommandCount: recorder.CommandCount,
            BeginCount: recorder.BeginCount,
            CommitCount: recorder.CommitCount,
            RollbackCount: recorder.RollbackCount,
            ReferentialIdentityLookupCount: recorder.Commands.Count(command =>
                command.CommandText.Contains("[dms].[ReferentialIdentity]", StringComparison.Ordinal)
            ),
            // The hydration batch is the only command that touches the root table and its child
            // collection together; every persist command targets exactly one table. Matching on both
            // table names avoids depending on how the batch opens, which differs by dialect: SQL
            // Server materializes a keyset relation first, so its hydration batch does not begin
            // with SELECT.
            HydrationBatchCount: recorder.Commands.Count(command =>
                command.CommandText.Contains("[edfi].[School]", StringComparison.Ordinal)
                && command.CommandText.Contains("[edfi].[SchoolAddress]", StringComparison.Ordinal)
            ),
            // The PUT capture predicate is the only place the aliased dms.Document row filters on the
            // external DocumentUuid; the POST capture reaches the same table through
            // ReferentialIdentity. The parameter suffix is allocator-issued, so the match is on the
            // stable prefix.
            DocumentUuidLookupCount: recorder.Commands.Count(command =>
                command.CommandText.Contains("d.[DocumentUuid] = @documentUuid", StringComparison.Ordinal)
            )
        );
}

public abstract class MssqlWriteSessionCommandStreamFixtureTestBase
{
    private protected MappingSet _mappingSet = null!;
    private protected MssqlGeneratedDdlTestDatabase _database = null!;
    private protected ServiceProvider _serviceProvider = null!;
    private protected RelationalWriteSessionCommandRecorder _recorder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            WriteSessionCommandStreamTestSupport.FixtureRelativePath
        );
        _mappingSet = fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = WriteSessionCommandStreamTestSupport.CreateServiceProvider();
        _recorder = _serviceProvider.GetRequiredService<RelationalWriteSessionCommandRecorder>();
        await SetUpTestAsync();
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

    protected abstract Task SetUpTestAsync();

    private protected IServiceScope CreateSelectedScope()
    {
        var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlWriteSessionCommandStream",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return scope;
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Write_Session_Command_Stream_For_A_Post_Create
    : MssqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("2b2b2b2b-0000-0000-0000-000000000001")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                _mappingSet,
                WriteSessionCommandStreamTestSupport.CreateRequestBody(2),
                _schoolDocumentUuid,
                "mssql-write-session-stream-create"
            )
        );
    }

    [Test]
    public void It_creates_the_document() => _result.Should().BeOfType<UpsertResult.InsertSuccess>();

    [Test]
    public void It_observes_the_in_session_target_lookup_that_the_recorder_previously_could_not_see() =>
        WriteSessionCommandStreamScenarios.AssertCreateStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            expectedTotalCommandCount: 5
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Production_First_Phase_With_A_Tvp_Reference_Lookup
    : MssqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("2b2b2b2b-0000-0000-0000-000000000099")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateBulkReferenceUpsertRequest(
                _mappingSet,
                _schoolDocumentUuid,
                MssqlReferenceLookupSmallListStrategy.BulkLookupThreshold
            )
        );
    }

    [Test]
    public void It_executes_capture_then_the_standalone_table_valued_lookup_in_the_same_transaction()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureReference>();
        _recorder.ShouldHaveCommandCount(2);
        _recorder
            .Commands[0]
            .CommandText.Should()
            .Contain("[dms].[ReferentialIdentity]")
            .And.NotContain("JOIN @referentialIds");
        _recorder.Commands[1].CommandText.Should().Contain("FROM @referentialIds");
        _recorder.Commands[1].Parameters.Should().ContainSingle();
        _recorder.ShouldHaveTransactionBoundary(
            expectedBeginCount: 1,
            expectedCommitCount: 0,
            expectedRollbackCount: 1
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Production_First_Phase_With_A_Small_Reference_Lookup
    : MssqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("2b2b2b2b-0000-0000-0000-000000000098")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateBulkReferenceUpsertRequest(
                _mappingSet,
                _schoolDocumentUuid,
                referenceCount: 1
            )
        );
    }

    [Test]
    public void It_embeds_capture_and_the_scalar_reference_lookup_in_one_production_command()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureReference>();
        _recorder.ShouldHaveCommandCount(1);
        _recorder.Commands[0].CommandText.Should().Contain("[dms].[ReferentialIdentity]");
        _recorder.Commands[0].Parameters.Should().HaveCount(3);
        _recorder.ShouldHaveTransactionBoundary(
            expectedBeginCount: 1,
            expectedCommitCount: 0,
            expectedRollbackCount: 1
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Write_Session_Command_Stream_For_A_Put_Update
    : MssqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("2b2b2b2b-0000-0000-0000-000000000002")
    );

    private UpdateResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using (var createScope = CreateSelectedScope())
        {
            var createRepository =
                createScope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

            await createRepository.UpsertDocument(
                WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                    _mappingSet,
                    WriteSessionCommandStreamTestSupport.CreateRequestBody(2),
                    _schoolDocumentUuid,
                    "mssql-write-session-stream-update-seed"
                )
            );
        }

        // Only the update's own command stream is under characterization.
        _recorder.Reset();

        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpdateDocumentById(
            WriteSessionCommandStreamTestSupport.CreateUpdateRequest(
                _mappingSet,
                WriteSessionCommandStreamTestSupport.CreateRequestBody(3),
                _schoolDocumentUuid,
                "mssql-write-session-stream-update"
            )
        );
    }

    [Test]
    public void It_updates_the_document() => _result.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_observes_the_hydration_batch_and_the_in_session_put_target_lookup() =>
        WriteSessionCommandStreamScenarios.AssertUpdateStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            expectedTotalCommandCount: 3
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Write_Session_Command_Stream_For_A_Post_As_Update
    : MssqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("2b2b2b2b-0000-0000-0000-000000000003")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using (var createScope = CreateSelectedScope())
        {
            var createRepository =
                createScope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

            await createRepository.UpsertDocument(
                WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                    _mappingSet,
                    WriteSessionCommandStreamTestSupport.CreateRequestBody(2),
                    _schoolDocumentUuid,
                    "mssql-write-session-stream-post-as-update-seed"
                )
            );
        }

        _recorder.Reset();

        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                _mappingSet,
                WriteSessionCommandStreamTestSupport.CreateRequestBody(3),
                new DocumentUuid(Guid.Parse("2b2b2b2b-0000-0000-0000-0000000000ff")),
                "mssql-write-session-stream-post-as-update"
            )
        );
    }

    [Test]
    public void It_updates_the_existing_document() => _result.Should().BeOfType<UpsertResult.UpdateSuccess>();

    [Test]
    public void It_observes_the_hydration_batch_and_the_single_in_session_target_lookup() =>
        WriteSessionCommandStreamScenarios.AssertPostAsUpdateStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            expectedTotalCommandCount: 3
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Write_Session_Command_Stream_For_A_Missing_Put_Target
    : MssqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _missingDocumentUuid = new(
        Guid.Parse("2b2b2b2b-0000-0000-0000-000000000004")
    );

    private UpdateResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpdateDocumentById(
            WriteSessionCommandStreamTestSupport.CreateUpdateRequest(
                _mappingSet,
                WriteSessionCommandStreamTestSupport.CreateRequestBody(3),
                _missingDocumentUuid,
                "mssql-write-session-stream-missing-put"
            )
        );
    }

    [Test]
    public void It_reports_the_document_as_not_existing() =>
        _result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();

    [Test]
    public void It_observes_the_missing_target_inside_the_transaction_and_rolls_back() =>
        WriteSessionCommandStreamScenarios.AssertMissingPutTargetStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder)
        );
}
