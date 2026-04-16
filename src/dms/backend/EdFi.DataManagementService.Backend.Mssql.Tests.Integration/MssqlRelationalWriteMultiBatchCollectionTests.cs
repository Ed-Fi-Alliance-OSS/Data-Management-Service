// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlMultiBatchCollectionsAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlMultiBatchCollectionsNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

internal sealed record MssqlMultiBatchCollectionPersistedDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MssqlMultiBatchCollectionPersistedSchoolRow(
    long DocumentId,
    long SchoolId,
    string? ShortName
);

internal sealed record MssqlMultiBatchCollectionPersistedSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record MssqlMultiBatchCollectionPersistedSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

internal sealed record MssqlMultiBatchCollectionPersistedState(
    MssqlMultiBatchCollectionPersistedDocumentRow Document,
    MssqlMultiBatchCollectionPersistedSchoolRow School,
    IReadOnlyList<MssqlMultiBatchCollectionPersistedSchoolAddressRow> Addresses
);

internal sealed record MssqlMultiBatchRecordedRelationalCommand(
    string CommandText,
    IReadOnlyDictionary<string, object?> ParametersByName
);

internal sealed class MssqlMultiBatchCommandRecorder
{
    private readonly List<MssqlMultiBatchRecordedRelationalCommand> _commands = [];

    public IReadOnlyList<MssqlMultiBatchRecordedRelationalCommand> Commands => _commands;

    public void Reset() => _commands.Clear();

    public void Record(RelationalCommand command)
    {
        Dictionary<string, object?> parametersByName = new(StringComparer.Ordinal);

        foreach (var parameter in command.Parameters)
        {
            parametersByName.Add(parameter.Name, parameter.Value);
        }

        _commands.Add(new MssqlMultiBatchRecordedRelationalCommand(command.CommandText, parametersByName));
    }
}

file sealed class RecordingMssqlRelationalWriteSessionFactory(
    IDmsInstanceSelection dmsInstanceSelection,
    IOptions<DatabaseOptions> databaseOptions,
    MssqlMultiBatchCommandRecorder commandRecorder
) : IRelationalWriteSessionFactory
{
    private readonly IDmsInstanceSelection _dmsInstanceSelection =
        dmsInstanceSelection ?? throw new ArgumentNullException(nameof(dmsInstanceSelection));
    private readonly IsolationLevel _isolationLevel =
        databaseOptions?.Value.IsolationLevel ?? throw new ArgumentNullException(nameof(databaseOptions));
    private readonly MssqlMultiBatchCommandRecorder _commandRecorder =
        commandRecorder ?? throw new ArgumentNullException(nameof(commandRecorder));

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var selectedInstance = _dmsInstanceSelection.GetSelectedDmsInstance();
        var connection = new SqlConnection(selectedInstance.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var transaction = await connection
                .BeginTransactionAsync(_isolationLevel, cancellationToken)
                .ConfigureAwait(false);

            return new RecordingMssqlRelationalWriteSession(
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

file sealed class RecordingMssqlRelationalWriteSession(
    IRelationalWriteSession innerSession,
    MssqlMultiBatchCommandRecorder commandRecorder
) : IRelationalWriteSession
{
    private readonly IRelationalWriteSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));
    private readonly MssqlMultiBatchCommandRecorder _commandRecorder =
        commandRecorder ?? throw new ArgumentNullException(nameof(commandRecorder));

    public DbConnection Connection => _innerSession.Connection;

    public DbTransaction Transaction => _innerSession.Transaction;

    public DbCommand CreateCommand(RelationalCommand command)
    {
        _commandRecorder.Record(command);
        return _innerSession.CreateCommand(command);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _innerSession.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _innerSession.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _innerSession.DisposeAsync();
}

file static class MssqlMultiBatchCollectionsIntegrationTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<MssqlMultiBatchCommandRecorder>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddScoped<IRelationalWriteSessionFactory, RecordingMssqlRelationalWriteSessionFactory>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static JsonNode CreateCreateRequestBody(int addressCount)
    {
        JsonArray addresses = [];

        for (var index = 0; index < addressCount; index++)
        {
            addresses.Add(new JsonObject { ["city"] = CreateCity(index) });
        }

        return new JsonObject
        {
            ["schoolId"] = 255901,
            ["shortName"] = "BATCH",
            ["addresses"] = addresses,
        };
    }

    public static JsonNode CreateUpdateRequestBody(int retainedAddressCount)
    {
        JsonArray addresses = [];

        for (var index = 0; index < retainedAddressCount; index++)
        {
            addresses.Add(new JsonObject { ["city"] = CreateCity(index) });
        }

        return new JsonObject
        {
            ["schoolId"] = 255901,
            ["shortName"] = "BATCH",
            ["addresses"] = addresses,
        };
    }

    public static JsonNode CreateCreateRequestBodyWithCollectionAlignedExtensions(int addressCount)
    {
        JsonArray addresses = [];
        JsonArray extensionAddresses = [];

        for (var index = 0; index < addressCount; index++)
        {
            addresses.Add(new JsonObject { ["city"] = CreateCity(index) });
            extensionAddresses.Add(
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["sample"] = new JsonObject { ["zone"] = CreateZone(index) },
                    },
                }
            );
        }

        return new JsonObject
        {
            ["schoolId"] = 255901,
            ["shortName"] = "BATCH-EXT",
            ["addresses"] = addresses,
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["addresses"] = extensionAddresses } },
        };
    }

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
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlMultiBatchCollectionsNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlMultiBatchCollectionsAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        JsonNode edfiDoc,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new UpdateRequest(
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
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlMultiBatchCollectionsNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlMultiBatchCollectionsAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    public static TableWritePlan GetSchoolAddressTablePlan(MappingSet mappingSet)
    {
        var resourceWritePlan = mappingSet.GetWritePlanOrThrow(SchoolResource);

        return resourceWritePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.Table == new DbTableName(new DbSchemaName("edfi"), "SchoolAddress")
        );
    }

    public static TableWritePlan GetSchoolExtensionAddressTablePlan(MappingSet mappingSet)
    {
        var resourceWritePlan = mappingSet.GetWritePlanOrThrow(SchoolResource);

        return resourceWritePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.Table
            == new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddress")
        );
    }

    public static async Task<MssqlMultiBatchCollectionPersistedState> ReadPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);

        return new MssqlMultiBatchCollectionPersistedState(document, school, addresses);
    }

    public static string CreateCity(int index) =>
        $"City-{index.ToString("D5", CultureInfo.InvariantCulture)}";

    public static string CreateZone(int index) =>
        $"Zone-{index.ToString("D5", CultureInfo.InvariantCulture)}";

    public static async Task<
        IReadOnlyList<MssqlMultiBatchCollectionPersistedSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT extension.[BaseCollectionItemId], extension.[School_DocumentId], extension.[Zone]
            FROM [sample].[SchoolExtensionAddress] AS extension
            INNER JOIN [edfi].[SchoolAddress] AS address
                ON address.[CollectionItemId] = extension.[BaseCollectionItemId]
                AND address.[School_DocumentId] = extension.[School_DocumentId]
            WHERE extension.[School_DocumentId] = @documentId
            ORDER BY address.[Ordinal], extension.[BaseCollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlMultiBatchCollectionPersistedSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
            ))
            .ToArray();
    }

    private static async Task<MssqlMultiBatchCollectionPersistedDocumentRow> ReadDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new MssqlMultiBatchCollectionPersistedDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<MssqlMultiBatchCollectionPersistedSchoolRow> ReadSchoolAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [SchoolId], [ShortName]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new MssqlMultiBatchCollectionPersistedSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<
        IReadOnlyList<MssqlMultiBatchCollectionPersistedSchoolAddressRow>
    > ReadSchoolAddressesAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [School_DocumentId], [Ordinal], [City]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlMultiBatchCollectionPersistedSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
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

internal abstract class MssqlMultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private protected MappingSet _mappingSet = null!;
    private protected MssqlGeneratedDdlTestDatabase _database = null!;
    private protected ServiceProvider _serviceProvider = null!;
    private protected MssqlMultiBatchCommandRecorder _commandRecorder = null!;

    [SetUp]
    public async Task SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlMultiBatchCollectionsIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        _serviceProvider = MssqlMultiBatchCollectionsIntegrationTestSupport.CreateServiceProvider();
        _commandRecorder = _serviceProvider.GetRequiredService<MssqlMultiBatchCommandRecorder>();
        OneTimeSetUpTest();
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

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    protected virtual void OneTimeSetUpTest() { }

    protected abstract Task SetUpTestAsync();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture
    : MssqlMultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000011")
    );

    private UpsertResult _result = null!;
    private MssqlMultiBatchCollectionPersistedState _persistedState = null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _requestedAddressCount;

    protected override void OneTimeSetUpTest()
    {
        var schoolAddressTablePlan =
            MssqlMultiBatchCollectionsIntegrationTestSupport.GetSchoolAddressTablePlan(_mappingSet);

        _maxRowsPerBatch = schoolAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _requestedAddressCount = _maxRowsPerBatch + 2;
    }

    protected override async Task SetUpTestAsync()
    {
        _result = await ExecuteCreateAsync();
        _persistedState = await MssqlMultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [Test]
    public void It_returns_insert_success_and_persists_the_full_large_collection()
    {
        _requestedAddressCount.Should().BeGreaterThan(_maxRowsPerBatch);
        _result.Should().BeOfType<UpsertResult.InsertSuccess>();
        _result.As<UpsertResult.InsertSuccess>().NewDocumentUuid.Should().Be(SchoolDocumentUuid);

        _persistedState.Document.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        _persistedState
            .Document.ResourceKeyId.Should()
            .Be(
                _mappingSet.ResourceKeyIdByResource[
                    MssqlMultiBatchCollectionsIntegrationTestSupport.SchoolResource
                ]
            );
        _persistedState
            .School.Should()
            .Be(
                new MssqlMultiBatchCollectionPersistedSchoolRow(
                    _persistedState.Document.DocumentId,
                    255901,
                    "BATCH"
                )
            );

        _persistedState.Addresses.Should().HaveCount(_requestedAddressCount);
        _persistedState
            .Addresses.Select(address => address.Ordinal)
            .Should()
            .Equal(Enumerable.Range(0, _requestedAddressCount));
        _persistedState.Addresses.Select(address => address.CollectionItemId).Should().OnlyHaveUniqueItems();

        _persistedState
            .Addresses[0]
            .Should()
            .Be(
                new MssqlMultiBatchCollectionPersistedSchoolAddressRow(
                    _persistedState.Addresses[0].CollectionItemId,
                    _persistedState.Document.DocumentId,
                    0,
                    MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCity(0)
                )
            );
        _persistedState
            .Addresses[^1]
            .Should()
            .Be(
                new MssqlMultiBatchCollectionPersistedSchoolAddressRow(
                    _persistedState.Addresses[^1].CollectionItemId,
                    _persistedState.Document.DocumentId,
                    _requestedAddressCount - 1,
                    MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCity(_requestedAddressCount - 1)
                )
            );
    }

    [Test]
    public void It_partitions_collection_id_reservation_and_insert_commands_using_the_compiled_batch_limit()
    {
        var reservationCommands = _commandRecorder
            .Commands.Where(command =>
                command.CommandText.Contains(
                    "NEXT VALUE FOR [dms].[CollectionItemIdSequence]",
                    StringComparison.Ordinal
                ) && command.CommandText.Contains("[sequence_request]", StringComparison.Ordinal)
            )
            .ToArray();

        reservationCommands.Should().HaveCount(2);
        reservationCommands
            .Select(command =>
                Convert.ToInt32(command.ParametersByName["@count"], CultureInfo.InvariantCulture)
            )
            .Should()
            .Equal(_maxRowsPerBatch, 2);

        var schoolAddressInsertCommands = _commandRecorder
            .Commands.Where(command =>
                command.CommandText.Contains("INSERT INTO [edfi].[SchoolAddress]", StringComparison.Ordinal)
            )
            .ToArray();

        schoolAddressInsertCommands.Should().HaveCount(2);
        schoolAddressInsertCommands
            .Select(command => command.ParametersByName.Count)
            .Should()
            .Equal(_maxRowsPerBatch * _parametersPerRow, 2 * _parametersPerRow);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteMultiBatchCollections",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestBody(
                    _requestedAddressCount
                ),
                SchoolDocumentUuid,
                "mssql-multi-batch-collections"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture
    : MssqlMultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000013")
    );

    private UpdateResult _result = null!;
    private MssqlMultiBatchCollectionPersistedState _persistedStateBeforeUpdate = null!;
    private MssqlMultiBatchCollectionPersistedState _persistedStateAfterUpdate = null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _createdAddressCount;

    protected override void OneTimeSetUpTest()
    {
        var schoolAddressTablePlan =
            MssqlMultiBatchCollectionsIntegrationTestSupport.GetSchoolAddressTablePlan(_mappingSet);

        _maxRowsPerBatch = schoolAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _createdAddressCount = _maxRowsPerBatch + 2;
    }

    protected override async Task SetUpTestAsync()
    {
        await ExecuteCreateAsync();

        _persistedStateBeforeUpdate =
            await MssqlMultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                SchoolDocumentUuid.Value
            );

        _commandRecorder.Reset();

        _result = await ExecuteUpdateAsync();
        _persistedStateAfterUpdate =
            await MssqlMultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                SchoolDocumentUuid.Value
            );
    }

    [Test]
    public void It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches()
    {
        _createdAddressCount.Should().BeGreaterThan(_maxRowsPerBatch);
        _result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _result.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);

        _persistedStateBeforeUpdate.Addresses.Should().HaveCount(_createdAddressCount);
        _persistedStateAfterUpdate.Addresses.Should().ContainSingle();
        _persistedStateAfterUpdate
            .Addresses[0]
            .Should()
            .Be(
                new MssqlMultiBatchCollectionPersistedSchoolAddressRow(
                    _persistedStateBeforeUpdate.Addresses[0].CollectionItemId,
                    _persistedStateAfterUpdate.Document.DocumentId,
                    0,
                    MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCity(0)
                )
            );
    }

    [Test]
    public void It_partitions_collection_delete_commands_using_the_compiled_batch_limit()
    {
        var deleteCommands = _commandRecorder
            .Commands.Where(command =>
                command.CommandText.Contains("delete from", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("SchoolAddress", StringComparison.Ordinal)
            )
            .ToArray();

        deleteCommands.Should().HaveCount(2);
        deleteCommands
            .Select(command => command.ParametersByName.Count)
            .Should()
            .Equal(_maxRowsPerBatch * _parametersPerRow, _parametersPerRow);
    }

    private async Task ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteMultiBatchCollectionDeletes",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        var createResult = await repository.UpsertDocument(
            MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestBody(
                    _createdAddressCount
                ),
                SchoolDocumentUuid,
                "mssql-multi-batch-collection-delete-create"
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteMultiBatchCollectionDeletes",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlMultiBatchCollectionsIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                MssqlMultiBatchCollectionsIntegrationTestSupport.CreateUpdateRequestBody(1),
                SchoolDocumentUuid,
                "mssql-multi-batch-collection-delete-update"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
internal class Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Aligned_Extension_Create_With_A_Focused_Stable_Key_Fixture
    : MssqlMultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000012")
    );

    private UpsertResult _result = null!;
    private MssqlMultiBatchCollectionPersistedState _persistedState = null!;
    private IReadOnlyList<MssqlMultiBatchCollectionPersistedSchoolExtensionAddressRow> _persistedExtensionAddresses =
        null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _requestedAddressCount;

    protected override void OneTimeSetUpTest()
    {
        var schoolExtensionAddressTablePlan =
            MssqlMultiBatchCollectionsIntegrationTestSupport.GetSchoolExtensionAddressTablePlan(_mappingSet);

        _maxRowsPerBatch = schoolExtensionAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolExtensionAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _requestedAddressCount = _maxRowsPerBatch + 2;
    }

    protected override async Task SetUpTestAsync()
    {
        _result = await ExecuteCreateAsync();
        _persistedState = await MssqlMultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        _persistedExtensionAddresses =
            await MssqlMultiBatchCollectionsIntegrationTestSupport.ReadSchoolExtensionAddressesAsync(
                _database,
                _persistedState.Document.DocumentId
            );
    }

    [Test]
    public void It_returns_insert_success_and_persists_the_full_large_collection_aligned_extension_scope()
    {
        _requestedAddressCount.Should().BeGreaterThan(_maxRowsPerBatch);
        _result.Should().BeOfType<UpsertResult.InsertSuccess>();
        _result.As<UpsertResult.InsertSuccess>().NewDocumentUuid.Should().Be(SchoolDocumentUuid);

        _persistedState.Addresses.Should().HaveCount(_requestedAddressCount);
        _persistedExtensionAddresses.Should().HaveCount(_requestedAddressCount);

        _persistedExtensionAddresses
            .Should()
            .Equal(
                _persistedState.Addresses.Select(
                    (address, index) =>
                        new MssqlMultiBatchCollectionPersistedSchoolExtensionAddressRow(
                            address.CollectionItemId,
                            _persistedState.Document.DocumentId,
                            MssqlMultiBatchCollectionsIntegrationTestSupport.CreateZone(index)
                        )
                )
            );
    }

    [Test]
    public void It_partitions_collection_aligned_extension_insert_commands_using_the_compiled_batch_limit()
    {
        var extensionInsertCommands = _commandRecorder
            .Commands.Where(command =>
                command.CommandText.Contains(
                    "INSERT INTO [sample].[SchoolExtensionAddress]",
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        extensionInsertCommands.Should().HaveCount(2);
        extensionInsertCommands
            .Select(command => command.ParametersByName.Count)
            .Should()
            .Equal(_maxRowsPerBatch * _parametersPerRow, 2 * _parametersPerRow);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteMultiBatchCollectionAlignedExtensions",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                MssqlMultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestBodyWithCollectionAlignedExtensions(
                    _requestedAddressCount
                ),
                SchoolDocumentUuid,
                "mssql-multi-batch-collection-aligned-extensions"
            )
        );
    }
}
