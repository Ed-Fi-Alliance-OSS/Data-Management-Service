// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
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

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Characterizes the complete ordered command stream a write request issues on its relational write
/// session. These counts are the observed baseline that the DMS-1332 verification document reports;
/// they are deliberately exact so a later change to the command stream is a visible diff.
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

    public static readonly ResourceInfo AddressTypeDescriptorResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("AddressTypeDescriptor"),
        IsDescriptor: true,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    /// <summary>The seeded descriptor that the create and POST-as-update requests reference.</summary>
    public const string AddressTypeDescriptorUri =
        NoProfileMultiBatchCollectionScenarios.OriginalAddressTypeDescriptorUri;

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddSingleton<RelationalWriteSessionCommandRecorder>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        // The reference resolver brings the document cache writer's retry adapter with it, and that
        // adapter takes the deadlock retry settings as a constructed dependency.
        services.AddSingleton(new DeadlockRetrySettings());
        services.AddPostgresqlBackendIntegrationTestServices();

        // Decorate the provider's own session factory so the recorder observes the real production
        // session, including BEGIN/COMMIT, rather than a substitute.
        services.AddScoped<IRelationalWriteSessionFactory>(
            serviceProvider => new RecordingRelationalWriteSessionFactory(
                ActivatorUtilities.CreateInstance<PostgresqlRelationalWriteSessionFactory>(serviceProvider),
                serviceProvider.GetRequiredService<RelationalWriteSessionCommandRecorder>()
            )
        );

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static JsonNode CreateRequestBody(int addressCount) =>
        NoProfileMultiBatchCollectionScenarios.CreateCollectionRequestBody(addressCount);

    /// <summary>
    /// The School body with the seeded AddressType descriptor on its first address, so the write's first phase
    /// has a resolvable natural-key lookup to embed.
    /// </summary>
    private static JsonNode CreateReferencingRequestBody(int addressCount)
    {
        var body = CreateRequestBody(addressCount);
        body["addresses"]![0]!["addressTypeDescriptor"] = AddressTypeDescriptorUri;

        return body;
    }

    private static DocumentIdentity AddressTypeDescriptorIdentity() =>
        new([
            new DocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                AddressTypeDescriptorUri
            ),
        ]);

    public static UpsertRequest CreateAddressTypeDescriptorUpsertRequest(MappingSet mappingSet) =>
        new(
            ResourceInfo: AddressTypeDescriptorResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: AddressTypeDescriptorIdentity(),
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                    AddressTypeDescriptorResourceInfo,
                    AddressTypeDescriptorIdentity()
                ),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: mappingSet,
            EdfiDoc: new JsonObject
            {
                ["namespace"] = "uri://ed-fi.org/AddressTypeDescriptor",
                ["codeValue"] = "Physical",
                ["shortDescription"] = "Physical",
            },
            Headers: [],
            TraceId: new TraceId("write-session-stream-descriptor-seed"),
            DocumentUuid: new DocumentUuid(Guid.NewGuid())
        )
        {
            ActionAuthorization =
                UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
        };

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

    /// <summary>
    /// A School POST whose body and extracted descriptor references both carry the seeded AddressType
    /// descriptor.
    /// </summary>
    public static UpsertRequest CreateUpsertRequest(
        MappingSet mappingSet,
        int addressCount,
        DocumentUuid documentUuid,
        string traceId,
        UpsertActionAuthorization? actionAuthorization = null
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo() with
            {
                DescriptorReferences =
                [
                    new DescriptorReference(
                        AddressTypeDescriptorResourceInfo,
                        AddressTypeDescriptorIdentity(),
                        new JsonPath("$.addresses[0].addressTypeDescriptor")
                    ),
                ],
            },
            MappingSet: mappingSet,
            EdfiDoc: CreateReferencingRequestBody(addressCount),
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        )
        {
            ActionAuthorization =
                actionAuthorization
                ?? UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
        };

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

    public static UpsertRequest CreateReferenceUpsertRequest(MappingSet mappingSet, DocumentUuid documentUuid)
    {
        var documentInfo = CreateSchoolDocumentInfo();
        var programResourceInfo = new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("Program"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false
        );
        var referencedIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.programName"), "missing-program"),
        ]);
        var documentReference = new DocumentReference(
            programResourceInfo,
            referencedIdentity,
            new JsonPath("$._ext.sample.addresses[0]._ext.sample.sponsorReferences[0].programReference")
        );

        return new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo with
            {
                DocumentReferences = [documentReference],
            },
            MappingSet: mappingSet,
            EdfiDoc: CreateRequestBody(0),
            Headers: [],
            TraceId: new TraceId("pg-write-session-reference-embedding"),
            DocumentUuid: documentUuid
        )
        {
            ActionAuthorization =
                UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
        };
    }

    /// <summary>
    /// Classifies recorded PostgreSQL command text into the provider-neutral summary the shared
    /// contract asserts over. Dialect text stays in this adapter, never in Tests.Common.
    /// </summary>
    public static WriteSessionCommandStreamSummary Summarize(
        RelationalWriteSessionCommandRecorder recorder
    ) =>
        Summarize(
            recorder.Commands,
            recorder.CommandCount,
            recorder.BeginCount,
            recorder.CommitCount,
            recorder.RollbackCount
        );

    /// <summary>
    /// Classifies one recorded command on its own, with no transaction boundary, so a test can assert
    /// what a single command of the stream carries.
    /// </summary>
    public static WriteSessionCommandStreamSummary SummarizeCommand(RecordedSessionCommand command) =>
        Summarize([command], totalCommandCount: 1, beginCount: 0, commitCount: 0, rollbackCount: 0);

    private static WriteSessionCommandStreamSummary Summarize(
        IReadOnlyList<RecordedSessionCommand> commands,
        int totalCommandCount,
        int beginCount,
        int commitCount,
        int rollbackCount
    ) =>
        new(
            TotalCommandCount: totalCommandCount,
            BeginCount: beginCount,
            CommitCount: commitCount,
            RollbackCount: rollbackCount,
            ReferentialIdentityLookupCount: commands.Count(command =>
                command.CommandText.Contains("dms.\"ReferentialIdentity\"", StringComparison.Ordinal)
            ),
            // The hydration batch reads the root table and its child collection together and modifies
            // neither. Touching several tables no longer separates it from persistence, because the DML
            // command co-batches every table's statements; being read-only still does. The same rule is
            // used by the SQL Server adapter so the two classifications stay comparable.
            HydrationBatchCount: commands.Count(command =>
                command.CommandText.Contains("\"edfi\".\"School\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"edfi\".\"SchoolAddress\"", StringComparison.Ordinal)
                && !ModifiesResourceTables(command.CommandText)
            ),
            // The PUT capture predicate is the only place the aliased dms."Document" row filters on
            // the external DocumentUuid; the POST capture filters on the resource's own natural key. The
            // parameter suffix is allocator-issued, so the match is on the stable prefix.
            DocumentUuidLookupCount: commands.Count(command =>
                command.CommandText.Contains("d.\"DocumentUuid\" = @documentUuid", StringComparison.Ordinal)
            ),
            NaturalKeyLookupCount: commands.Count(command =>
                command.CommandText.Contains(NaturalKeyLookupMarker, StringComparison.Ordinal)
            ),
            NaturalKeyCaptureCount: commands.Count(command =>
                command.CommandText.Contains(NaturalKeyCaptureMarker, StringComparison.Ordinal)
            )
        );

    /// <summary>
    /// The POST target capture binds each own-natural-key value as a plain scalar named <c>@nk{n}</c>. No other
    /// statement uses that prefix, and the composite rewrite only suffixes the name, so the prefix survives.
    /// </summary>
    public const string NaturalKeyCaptureMarker = "@nk";

    /// <summary>
    /// A natural-key lookup group reads its bound key arrays through a leftmost <c>unnest</c> of parameters named
    /// <c>@g{groupOrdinal}_...</c>. The composite rewrite only suffixes those names, so the prefix survives.
    /// </summary>
    public const string NaturalKeyLookupMarker = "unnest(@g";

    /// <summary>
    /// Whether any statement in the command modifies a resource table. Scoped to the resource schemas
    /// deliberately: a hydration batch may materialize its keyset with a temporary insert of its own, which says
    /// nothing about whether it persists anything.
    /// </summary>
    private static bool ModifiesResourceTables(string commandText) =>
        Array.Exists(
            ResourceTableDmlPrefixes,
            prefix => commandText.Contains(prefix, StringComparison.OrdinalIgnoreCase)
        );

    private static readonly string[] ResourceTableDmlPrefixes =
    [
        "INSERT INTO \"edfi\".",
        "INSERT INTO \"sample\".",
        "UPDATE \"edfi\".",
        "UPDATE \"sample\".",
        "DELETE FROM \"edfi\".",
        "DELETE FROM \"sample\".",
    ];
}

public abstract class PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private protected MappingSet _mappingSet = null!;
    private protected PostgresqlGeneratedDdlTestDatabase _database = null!;
    private protected ServiceProvider _serviceProvider = null!;
    private protected RelationalWriteSessionCommandRecorder _recorder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            WriteSessionCommandStreamTestSupport.FixtureRelativePath
        );
        _mappingSet = fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = WriteSessionCommandStreamTestSupport.CreateServiceProvider();
        _recorder = _serviceProvider.GetRequiredService<RelationalWriteSessionCommandRecorder>();
        await SeedAddressTypeDescriptorAsync();

        // Only the scenario's own command stream is under characterization.
        _recorder.Reset();
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

    private async Task SeedAddressTypeDescriptorAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        var result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateAddressTypeDescriptorUpsertRequest(_mappingSet)
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private protected IServiceScope CreateSelectedScope()
    {
        var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlWriteSessionCommandStream",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return scope;
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Write_Session_Command_Stream_For_A_Post_Create
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("1a1a1a1a-0000-0000-0000-000000000001")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                _mappingSet,
                addressCount: 2,
                _schoolDocumentUuid,
                "pg-write-session-stream-create"
            )
        );
    }

    [Test]
    public void It_creates_the_document() => _result.Should().BeOfType<UpsertResult.InsertSuccess>();

    [Test]
    public void It_observes_the_in_session_target_lookup_that_the_recorder_previously_could_not_see() =>
        WriteSessionCommandStreamScenarios.AssertCreateStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            expectedTotalCommandCount: 2
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Production_First_Phase_With_A_Reference_Lookup
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("1a1a1a1a-0000-0000-0000-000000000099")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateReferenceUpsertRequest(
                _mappingSet,
                _schoolDocumentUuid
            )
        );
    }

    [Test]
    public void It_embeds_capture_and_the_natural_key_reference_lookup_in_one_production_command()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureReference>();
        _recorder.ShouldHaveCommandCount(1);
        _recorder
            .Commands[0]
            .CommandText.Should()
            .Contain(WriteSessionCommandStreamTestSupport.NaturalKeyLookupMarker);
        // The capture's two parameters plus the lookup group's ordinal array and its one key array.
        _recorder.Commands[0].Parameters.Should().HaveCount(4);
        _recorder.ShouldHaveTransactionBoundary(
            expectedBeginCount: 1,
            expectedCommitCount: 0,
            expectedRollbackCount: 1
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Write_Session_Command_Stream_For_A_Put_Update
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("1a1a1a1a-0000-0000-0000-000000000002")
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
                    addressCount: 2,
                    _schoolDocumentUuid,
                    "pg-write-session-stream-update-seed"
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
                "pg-write-session-stream-update"
            )
        );
    }

    [Test]
    public void It_updates_the_document() => _result.Should().BeOfType<UpdateResult.UpdateSuccess>();

    [Test]
    public void It_observes_the_hydration_batch_and_the_in_session_put_target_lookup() =>
        WriteSessionCommandStreamScenarios.AssertUpdateStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            expectedTotalCommandCount: 2
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Write_Session_Command_Stream_For_A_Post_As_Update
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("1a1a1a1a-0000-0000-0000-000000000003")
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
                    addressCount: 2,
                    _schoolDocumentUuid,
                    "pg-write-session-stream-post-as-update-seed"
                )
            );
        }

        _recorder.Reset();

        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                _mappingSet,
                addressCount: 3,
                new DocumentUuid(Guid.Parse("1a1a1a1a-0000-0000-0000-0000000000ff")),
                "pg-write-session-stream-post-as-update"
            )
        );
    }

    [Test]
    public void It_updates_the_existing_document() => _result.Should().BeOfType<UpsertResult.UpdateSuccess>();

    [Test]
    public void It_observes_the_hydration_batch_and_the_single_in_session_target_lookup() =>
        WriteSessionCommandStreamScenarios.AssertPostAsUpdateStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            expectedTotalCommandCount: 2
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Write_Session_Command_Stream_For_A_Missing_Put_Target
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
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
                "postgresql-write-session-stream-missing-put"
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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Write_Session_Command_Stream_For_An_Ordered_Segments_Post_Create
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("1a1a1a1a-0000-0000-0000-000000000005")
    );

    private UpsertResult _result = null!;

    protected override async Task SetUpTestAsync()
    {
        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                _mappingSet,
                addressCount: 2,
                _schoolDocumentUuid,
                "postgresql-write-session-stream-ordered-create",
                UpsertActionAuthorizationTestSupport.CreateOnlyWithNoFurtherAuthorizationRequired
            )
        );
    }

    [Test]
    public void It_creates_the_document() => _result.Should().BeOfType<UpsertResult.InsertSuccess>();

    [Test]
    public void It_keeps_the_ordered_segments_round_trips_with_the_capture_alone_first() =>
        WriteSessionCommandStreamScenarios.AssertOrderedSegmentsPostStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            WriteSessionCommandStreamTestSupport.SummarizeCommand(_recorder.Commands[0]),
            expectedTotalCommandCount: 3,
            expectedHydrationBatchCount: 0
        );
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Write_Session_Command_Stream_For_An_Ordered_Segments_Post_As_Update
    : PostgresqlWriteSessionCommandStreamFixtureTestBase
{
    private static readonly DocumentUuid _schoolDocumentUuid = new(
        Guid.Parse("1a1a1a1a-0000-0000-0000-000000000006")
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
                    addressCount: 2,
                    _schoolDocumentUuid,
                    "postgresql-write-session-stream-ordered-post-as-update-seed"
                )
            );
        }

        _recorder.Reset();

        using var scope = CreateSelectedScope();
        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        _result = await repository.UpsertDocument(
            WriteSessionCommandStreamTestSupport.CreateUpsertRequest(
                _mappingSet,
                addressCount: 3,
                new DocumentUuid(Guid.Parse("1a1a1a1a-0000-0000-0000-0000000000fe")),
                "postgresql-write-session-stream-ordered-post-as-update",
                UpsertActionAuthorizationTestSupport.UpdateOnlyWithNoFurtherAuthorizationRequired
            )
        );
    }

    [Test]
    public void It_updates_the_existing_document() => _result.Should().BeOfType<UpsertResult.UpdateSuccess>();

    [Test]
    public void It_keeps_the_ordered_segments_round_trips_with_the_capture_alone_first() =>
        WriteSessionCommandStreamScenarios.AssertOrderedSegmentsPostStreamIsFullyObserved(
            WriteSessionCommandStreamTestSupport.Summarize(_recorder),
            WriteSessionCommandStreamTestSupport.SummarizeCommand(_recorder.Commands[0]),
            expectedTotalCommandCount: 4,
            expectedHydrationBatchCount: 1
        );
}
