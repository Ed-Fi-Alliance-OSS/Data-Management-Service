// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
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
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record MultiBatchCollectionPersistedDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MultiBatchCollectionPersistedSchoolRow(
    long DocumentId,
    long SchoolId,
    string? ShortName
);

internal sealed record MultiBatchCollectionPersistedSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record MultiBatchCollectionPersistedSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

internal sealed record MultiBatchCollectionPersistedState(
    MultiBatchCollectionPersistedDocumentRow Document,
    MultiBatchCollectionPersistedSchoolRow School,
    IReadOnlyList<MultiBatchCollectionPersistedSchoolAddressRow> Addresses
);

internal sealed record RecordedRelationalCommand(
    string CommandText,
    IReadOnlyDictionary<string, object?> ParametersByName
);

internal sealed class MultiBatchCommandRecorder
{
    private readonly List<RecordedRelationalCommand> _commands = [];

    public IReadOnlyList<RecordedRelationalCommand> Commands => _commands;

    public void Reset() => _commands.Clear();

    public void Record(RelationalCommand command)
    {
        Dictionary<string, object?> parametersByName = new(StringComparer.Ordinal);

        foreach (var parameter in command.Parameters)
        {
            parametersByName.Add(parameter.Name, parameter.Value);
        }

        _commands.Add(new RecordedRelationalCommand(command.CommandText, parametersByName));
    }
}

file sealed class RecordingPostgresqlRelationalWriteSessionFactory(
    NpgsqlDataSourceProvider dataSourceProvider,
    IOptions<DatabaseOptions> databaseOptions,
    MultiBatchCommandRecorder commandRecorder
) : IRelationalWriteSessionFactory
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));
    private readonly IsolationLevel _isolationLevel =
        databaseOptions?.Value.IsolationLevel ?? throw new ArgumentNullException(nameof(databaseOptions));
    private readonly MultiBatchCommandRecorder _commandRecorder =
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

            return new RecordingRelationalWriteSession(
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

file sealed class RecordingRelationalWriteSession(
    IRelationalWriteSession innerSession,
    MultiBatchCommandRecorder commandRecorder
) : IRelationalWriteSession
{
    private readonly IRelationalWriteSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));
    private readonly MultiBatchCommandRecorder _commandRecorder =
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

file static class MultiBatchCollectionsIntegrationTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
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
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddSingleton<MultiBatchCommandRecorder>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddScoped<
            IRelationalWriteSessionFactory,
            RecordingPostgresqlRelationalWriteSessionFactory
        >();
        services.AddPostgresqlReferenceResolver();

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
            DocumentUuid: documentUuid
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
            DocumentUuid: documentUuid
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

    public static async Task<MultiBatchCollectionPersistedState> ReadPersistedStateAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);

        return new MultiBatchCollectionPersistedState(document, school, addresses);
    }

    public static string CreateCity(int index) => NoProfileMultiBatchCollectionScenarios.CreateCity(index);

    public static string CreateZone(int index) => NoProfileMultiBatchCollectionScenarios.CreateZone(index);

    // Translate recorded PostgreSQL commands into the provider-neutral batch summaries the shared
    // contract asserts over. Dialect command text stays in this adapter, not in Common.
    public static IReadOnlyList<int> ReservationRowCounts(MultiBatchCommandRecorder recorder) =>
        recorder
            .Commands.Where(command =>
                command.CommandText.Contains("generate_series(1, @count)", StringComparison.Ordinal)
            )
            .Select(command =>
                Convert.ToInt32(command.ParametersByName["@count"], CultureInfo.InvariantCulture)
            )
            .ToArray();

    public static IReadOnlyList<int> SchoolAddressInsertParameterCounts(MultiBatchCommandRecorder recorder) =>
        InsertParameterCounts(recorder, "INSERT INTO \"edfi\".\"SchoolAddress\"");

    public static IReadOnlyList<int> SchoolExtensionAddressInsertParameterCounts(
        MultiBatchCommandRecorder recorder
    ) => InsertParameterCounts(recorder, "INSERT INTO \"sample\".\"SchoolExtensionAddress\"");

    public static IReadOnlyList<int> SchoolAddressDeleteParameterCounts(MultiBatchCommandRecorder recorder) =>
        recorder
            .Commands.Where(command =>
                command.CommandText.Contains("delete from", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("SchoolAddress", StringComparison.Ordinal)
            )
            .Select(command => command.ParametersByName.Count)
            .ToArray();

    // Base-collection update-by-stable-row-identity commands against the edfi.SchoolAddress table only, so the
    // Document/School single-row updates and any sample.SchoolExtensionAddress commands are excluded.
    public static IReadOnlyList<int> SchoolAddressUpdateParameterCounts(MultiBatchCommandRecorder recorder) =>
        recorder
            .Commands.Where(command =>
                command.CommandText.Contains("update", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"edfi\".\"SchoolAddress\"", StringComparison.Ordinal)
            )
            .Select(command => command.ParametersByName.Count)
            .ToArray();

    // --- Changed-descriptor multi-batch update scenario support (NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions) ---

    public static readonly QualifiedResourceName AddressTypeDescriptorResource = new(
        "Ed-Fi",
        "AddressTypeDescriptor"
    );

    public const string OriginalAddressTypeDescriptorUri = "uri://ed-fi.org/AddressTypeDescriptor#Physical";
    public const string ReplacementAddressTypeDescriptorUri = "uri://ed-fi.org/AddressTypeDescriptor#Mailing";

    public static JsonNode CreateAddressRequestBodyWithDescriptor(
        int addressCount,
        string addressTypeDescriptorUri
    )
    {
        JsonArray addresses = [];

        for (var index = 0; index < addressCount; index++)
        {
            addresses.Add(
                new JsonObject
                {
                    ["addressTypeDescriptor"] = addressTypeDescriptorUri,
                    ["city"] = CreateCity(index),
                }
            );
        }

        return new JsonObject
        {
            ["schoolId"] = 255901,
            ["shortName"] = "BATCH",
            ["addresses"] = addresses,
        };
    }

    // The focused DDL fixture's ApiSchema is trimmed to what the DDL emitter needs, so the ResourceSchema
    // extraction helpers are unavailable. The School identity is fixed ($.schoolId) and the per-address
    // AddressType descriptor references are constructed directly so the reference resolver can populate each
    // SchoolAddress row's AddressTypeDescriptorId.
    public static DocumentInfo CreateSchoolDocumentInfoWithAddressDescriptors(
        int addressCount,
        string addressTypeDescriptorUri
    )
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);
        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("AddressTypeDescriptor"),
            IsDescriptor: true
        );
        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                addressTypeDescriptorUri.ToLowerInvariant()
            ),
        ]);
        var descriptorReferentialId = ReferentialIdCalculator.ReferentialIdFrom(
            descriptorResourceInfo,
            descriptorIdentity
        );

        DescriptorReference[] descriptorReferences =
        [
            .. Enumerable
                .Range(0, addressCount)
                .Select(index => new DescriptorReference(
                    ResourceInfo: descriptorResourceInfo,
                    DocumentIdentity: descriptorIdentity,
                    ReferentialId: descriptorReferentialId,
                    Path: new JsonPath($"$.addresses[{index}].addressTypeDescriptor")
                )),
        ];

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: descriptorReferences,
            SuperclassIdentity: null
        );
    }

    public static UpsertRequest CreateCreateRequestWithDescriptor(
        MappingSet mappingSet,
        JsonNode edfiDoc,
        int addressCount,
        string addressTypeDescriptorUri,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfoWithAddressDescriptors(
                addressCount,
                addressTypeDescriptorUri
            ),
            MappingSet: mappingSet,
            EdfiDoc: edfiDoc,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

    public static UpdateRequest CreateUpdateRequestWithDescriptor(
        MappingSet mappingSet,
        JsonNode edfiDoc,
        int addressCount,
        string addressTypeDescriptorUri,
        DocumentUuid documentUuid,
        string traceId
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfoWithAddressDescriptors(
                addressCount,
                addressTypeDescriptorUri
            ),
            MappingSet: mappingSet,
            EdfiDoc: edfiDoc,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

    public static async Task<long> SeedAddressTypeDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        Guid documentUuid,
        string uri,
        string codeValue
    )
    {
        short resourceKeyId = mappingSet.ResourceKeyIdByResource[AddressTypeDescriptorResource];

        long documentId = await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", "uri://ed-fi.org/AddressTypeDescriptor"),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", codeValue),
            new NpgsqlParameter("description", codeValue),
            new NpgsqlParameter("discriminator", "Ed-Fi:AddressTypeDescriptor"),
            new NpgsqlParameter("uri", uri)
        );

        var referentialId = CreateDescriptorReferentialId("Ed-Fi", "AddressTypeDescriptor", uri);
        var existing = await database.QueryRowsAsync(
            """
            SELECT "ReferentialId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
              AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        if (existing.Count == 0)
        {
            await database.ExecuteNonQueryAsync(
                """
                INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
                VALUES (@referentialId, @documentId, @resourceKeyId);
                """,
                new NpgsqlParameter("referentialId", referentialId.Value),
                new NpgsqlParameter("documentId", documentId),
                new NpgsqlParameter("resourceKeyId", resourceKeyId)
            );
        }

        return documentId;
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string uri
    ) =>
        ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(new ProjectName(projectName), new ResourceName(resourceName), true),
            new DocumentIdentity([
                new DocumentIdentityElement(
                    new JsonPath(DocumentIdentity.DescriptorIdentityJsonPath.Value),
                    uri.ToLowerInvariant()
                ),
            ])
        );

    public static async Task<
        IReadOnlyList<NoProfileMultiBatchCollectionScenarios.SchoolAddressWithDescriptorRow>
    > ReadSchoolAddressesWithDescriptorAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City", "AddressTypeDescriptor_DescriptorId"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new NoProfileMultiBatchCollectionScenarios.SchoolAddressWithDescriptorRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City"),
                GetInt64(row, "AddressTypeDescriptor_DescriptorId")
            ))
            .ToArray();
    }

    public static NoProfileMultiBatchCollectionScenarios.DocumentRow ToNeutral(
        MultiBatchCollectionPersistedDocumentRow document
    ) => new(document.DocumentId, document.DocumentUuid, document.ResourceKeyId, document.ContentVersion);

    public static NoProfileMultiBatchCollectionScenarios.SchoolRow ToNeutral(
        MultiBatchCollectionPersistedSchoolRow school
    ) => new(school.DocumentId, school.SchoolId, school.ShortName);

    public static NoProfileMultiBatchCollectionScenarios.SchoolAddressRow ToNeutral(
        MultiBatchCollectionPersistedSchoolAddressRow address
    ) => new(address.CollectionItemId, address.SchoolDocumentId, address.Ordinal, address.City);

    public static NoProfileMultiBatchCollectionScenarios.SchoolExtensionAddressRow ToNeutral(
        MultiBatchCollectionPersistedSchoolExtensionAddressRow extensionAddress
    ) => new(extensionAddress.BaseCollectionItemId, extensionAddress.SchoolDocumentId, extensionAddress.Zone);

    private static IReadOnlyList<int> InsertParameterCounts(
        MultiBatchCommandRecorder recorder,
        string insertSnippet
    ) =>
        recorder
            .Commands.Where(command => command.CommandText.Contains(insertSnippet, StringComparison.Ordinal))
            .Select(command => command.ParametersByName.Count)
            .ToArray();

    private static async Task<MultiBatchCollectionPersistedDocumentRow> ReadDocumentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new MultiBatchCollectionPersistedDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<MultiBatchCollectionPersistedSchoolRow> ReadSchoolAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId", "ShortName"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new MultiBatchCollectionPersistedSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<
        IReadOnlyList<MultiBatchCollectionPersistedSchoolAddressRow>
    > ReadSchoolAddressesAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new MultiBatchCollectionPersistedSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    public static async Task<
        IReadOnlyList<MultiBatchCollectionPersistedSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT extension."BaseCollectionItemId", extension."School_DocumentId", extension."Zone"
            FROM "sample"."SchoolExtensionAddress" AS extension
            INNER JOIN "edfi"."SchoolAddress" AS address
                ON address."CollectionItemId" = extension."BaseCollectionItemId"
                AND address."School_DocumentId" = extension."School_DocumentId"
            WHERE extension."School_DocumentId" = @documentId
            ORDER BY address."Ordinal", extension."BaseCollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new MultiBatchCollectionPersistedSchoolExtensionAddressRow(
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

public abstract class MultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private protected MappingSet _mappingSet = null!;
    private protected PostgresqlGeneratedDdlTestDatabase _database = null!;
    private protected ServiceProvider _serviceProvider = null!;
    private protected MultiBatchCommandRecorder _commandRecorder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MultiBatchCollectionsIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        await OneTimeSetUpTestAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = MultiBatchCollectionsIntegrationTestSupport.CreateServiceProvider();
        _commandRecorder = _serviceProvider.GetRequiredService<MultiBatchCommandRecorder>();
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

    protected virtual Task OneTimeSetUpTestAsync() => Task.CompletedTask;

    protected abstract Task SetUpTestAsync();
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture
    : MultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001")
    );

    private UpsertResult _result = null!;
    private MultiBatchCollectionPersistedState _persistedState = null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _requestedAddressCount;

    protected override Task OneTimeSetUpTestAsync()
    {
        var schoolAddressTablePlan = MultiBatchCollectionsIntegrationTestSupport.GetSchoolAddressTablePlan(
            _mappingSet
        );

        _maxRowsPerBatch = schoolAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _requestedAddressCount = _maxRowsPerBatch + 2;

        return Task.CompletedTask;
    }

    protected override async Task SetUpTestAsync()
    {
        _result = await ExecuteCreateAsync();
        _persistedState = await MultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [Test]
    public void It_returns_insert_success_and_persists_the_full_large_collection() =>
        NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionCreatePersisted(
            _result,
            SchoolDocumentUuid,
            _mappingSet,
            _maxRowsPerBatch,
            _requestedAddressCount,
            MultiBatchCollectionsIntegrationTestSupport.ToNeutral(_persistedState.Document),
            MultiBatchCollectionsIntegrationTestSupport.ToNeutral(_persistedState.School),
            [
                .. _persistedState.Addresses.Select(address =>
                    MultiBatchCollectionsIntegrationTestSupport.ToNeutral(address)
                ),
            ]
        );

    [Test]
    public void It_partitions_collection_id_reservation_and_insert_commands_using_the_compiled_batch_limit() =>
        NoProfileMultiBatchCollectionScenarios.AssertCreateBatchPartitions(
            MultiBatchCollectionsIntegrationTestSupport.ReservationRowCounts(_commandRecorder),
            MultiBatchCollectionsIntegrationTestSupport.SchoolAddressInsertParameterCounts(_commandRecorder),
            _maxRowsPerBatch,
            _parametersPerRow
        );

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteMultiBatchCollections",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestBody(_requestedAddressCount),
                SchoolDocumentUuid,
                "pg-multi-batch-collections"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture
    : MultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000003")
    );

    private UpdateResult _result = null!;
    private MultiBatchCollectionPersistedState _persistedStateBeforeUpdate = null!;
    private MultiBatchCollectionPersistedState _persistedStateAfterUpdate = null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _createdAddressCount;

    protected override Task OneTimeSetUpTestAsync()
    {
        var schoolAddressTablePlan = MultiBatchCollectionsIntegrationTestSupport.GetSchoolAddressTablePlan(
            _mappingSet
        );

        _maxRowsPerBatch = schoolAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _createdAddressCount = _maxRowsPerBatch + 2;

        return Task.CompletedTask;
    }

    protected override async Task SetUpTestAsync()
    {
        await ExecuteCreateAsync();

        _persistedStateBeforeUpdate =
            await MultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                SchoolDocumentUuid.Value
            );

        _commandRecorder.Reset();

        _result = await ExecuteUpdateAsync();
        _persistedStateAfterUpdate =
            await MultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                SchoolDocumentUuid.Value
            );
    }

    [Test]
    public void It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches() =>
        NoProfileMultiBatchCollectionScenarios.AssertMultiBatchDeleteUpdateReducedToRetainedRow(
            _result,
            SchoolDocumentUuid,
            _persistedStateAfterUpdate.Document.DocumentId,
            _maxRowsPerBatch,
            _createdAddressCount,
            [.. _persistedStateBeforeUpdate.Addresses.Select(ToNeutralAddress)],
            [.. _persistedStateAfterUpdate.Addresses.Select(ToNeutralAddress)],
            MultiBatchCollectionsIntegrationTestSupport.CreateCity(0)
        );

    private static NoProfileUpdateSemanticsScenarios.SchoolAddressRow ToNeutralAddress(
        MultiBatchCollectionPersistedSchoolAddressRow address
    ) => new(address.CollectionItemId, address.SchoolDocumentId, address.Ordinal, address.City);

    [Test]
    public void It_partitions_collection_delete_commands_using_the_compiled_batch_limit() =>
        NoProfileMultiBatchCollectionScenarios.AssertDeleteBatchPartitions(
            MultiBatchCollectionsIntegrationTestSupport.SchoolAddressDeleteParameterCounts(_commandRecorder),
            _maxRowsPerBatch,
            _parametersPerRow
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
                    Name: "PostgresqlRelationalWriteMultiBatchCollectionDeletes",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        var createResult = await repository.UpsertDocument(
            MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestBody(_createdAddressCount),
                SchoolDocumentUuid,
                "pg-multi-batch-collection-delete-create"
            )
        );

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
                    Name: "PostgresqlRelationalWriteMultiBatchCollectionDeletes",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MultiBatchCollectionsIntegrationTestSupport.CreateUpdateRequest(
                _mappingSet,
                MultiBatchCollectionsIntegrationTestSupport.CreateUpdateRequestBody(1),
                SchoolDocumentUuid,
                "pg-multi-batch-collection-delete-update"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Aligned_Extension_Create_With_A_Focused_Stable_Key_Fixture
    : MultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000002")
    );

    private UpsertResult _result = null!;
    private MultiBatchCollectionPersistedState _persistedState = null!;
    private IReadOnlyList<MultiBatchCollectionPersistedSchoolExtensionAddressRow> _persistedExtensionAddresses =
        null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _requestedAddressCount;

    protected override Task OneTimeSetUpTestAsync()
    {
        var schoolExtensionAddressTablePlan =
            MultiBatchCollectionsIntegrationTestSupport.GetSchoolExtensionAddressTablePlan(_mappingSet);

        _maxRowsPerBatch = schoolExtensionAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolExtensionAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _requestedAddressCount = _maxRowsPerBatch + 2;

        return Task.CompletedTask;
    }

    protected override async Task SetUpTestAsync()
    {
        _result = await ExecuteCreateAsync();
        _persistedState = await MultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        _persistedExtensionAddresses =
            await MultiBatchCollectionsIntegrationTestSupport.ReadSchoolExtensionAddressesAsync(
                _database,
                _persistedState.Document.DocumentId
            );
    }

    [Test]
    public void It_returns_insert_success_and_persists_the_full_large_collection_aligned_extension_scope() =>
        NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionAlignedExtensionCreatePersisted(
            _result,
            SchoolDocumentUuid,
            _maxRowsPerBatch,
            _requestedAddressCount,
            MultiBatchCollectionsIntegrationTestSupport.ToNeutral(_persistedState.Document),
            [
                .. _persistedState.Addresses.Select(address =>
                    MultiBatchCollectionsIntegrationTestSupport.ToNeutral(address)
                ),
            ],
            [
                .. _persistedExtensionAddresses.Select(extensionAddress =>
                    MultiBatchCollectionsIntegrationTestSupport.ToNeutral(extensionAddress)
                ),
            ]
        );

    [Test]
    public void It_partitions_collection_aligned_extension_insert_commands_using_the_compiled_batch_limit() =>
        NoProfileMultiBatchCollectionScenarios.AssertAlignedExtensionInsertBatchPartitions(
            MultiBatchCollectionsIntegrationTestSupport.SchoolExtensionAddressInsertParameterCounts(
                _commandRecorder
            ),
            _maxRowsPerBatch,
            _parametersPerRow
        );

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteMultiBatchCollectionAlignedExtensions",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequest(
                _mappingSet,
                MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestBodyWithCollectionAlignedExtensions(
                    _requestedAddressCount
                ),
                SchoolDocumentUuid,
                "pg-multi-batch-collection-aligned-extensions"
            )
        );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture
    : MultiBatchCollectionsGeneratedDdlFixtureTestBase
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("0f0f0f0f-0000-0000-0000-000000000004")
    );
    private static readonly Guid OriginalDescriptorDocumentUuid = Guid.Parse(
        "0f0f0f0f-0000-0000-0000-0000000000d1"
    );
    private static readonly Guid ReplacementDescriptorDocumentUuid = Guid.Parse(
        "0f0f0f0f-0000-0000-0000-0000000000d2"
    );

    private long _originalDescriptorId;
    private long _replacementDescriptorId;
    private long _documentId;
    private UpdateResult _result = null!;
    private IReadOnlyList<NoProfileMultiBatchCollectionScenarios.SchoolAddressWithDescriptorRow> _addressesBefore =
        null!;
    private IReadOnlyList<NoProfileMultiBatchCollectionScenarios.SchoolAddressWithDescriptorRow> _addressesAfter =
        null!;
    private int _maxRowsPerBatch;
    private int _parametersPerRow;
    private int _createdAddressCount;

    protected override Task OneTimeSetUpTestAsync()
    {
        var schoolAddressTablePlan = MultiBatchCollectionsIntegrationTestSupport.GetSchoolAddressTablePlan(
            _mappingSet
        );

        _maxRowsPerBatch = schoolAddressTablePlan.BulkInsertBatching.MaxRowsPerBatch;
        _parametersPerRow = schoolAddressTablePlan.BulkInsertBatching.ParametersPerRow;
        _createdAddressCount = _maxRowsPerBatch + 2;

        return Task.CompletedTask;
    }

    protected override async Task SetUpTestAsync()
    {
        _originalDescriptorId =
            await MultiBatchCollectionsIntegrationTestSupport.SeedAddressTypeDescriptorAsync(
                _database,
                _mappingSet,
                OriginalDescriptorDocumentUuid,
                MultiBatchCollectionsIntegrationTestSupport.OriginalAddressTypeDescriptorUri,
                "Physical"
            );
        _replacementDescriptorId =
            await MultiBatchCollectionsIntegrationTestSupport.SeedAddressTypeDescriptorAsync(
                _database,
                _mappingSet,
                ReplacementDescriptorDocumentUuid,
                MultiBatchCollectionsIntegrationTestSupport.ReplacementAddressTypeDescriptorUri,
                "Mailing"
            );

        await ExecuteCreateAsync();

        var stateBeforeUpdate = await MultiBatchCollectionsIntegrationTestSupport.ReadPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
        _documentId = stateBeforeUpdate.Document.DocumentId;
        _addressesBefore =
            await MultiBatchCollectionsIntegrationTestSupport.ReadSchoolAddressesWithDescriptorAsync(
                _database,
                _documentId
            );

        // Only the changed PUT's commands must be observed for the update-batch partition assertion.
        _commandRecorder.Reset();

        _result = await ExecuteUpdateAsync();
        _addressesAfter =
            await MultiBatchCollectionsIntegrationTestSupport.ReadSchoolAddressesWithDescriptorAsync(
                _database,
                _documentId
            );
    }

    [Test]
    public void It_returns_update_success_and_applies_the_changed_descriptor_to_every_row() =>
        NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionChangedDescriptorUpdatePersisted(
            _result,
            SchoolDocumentUuid,
            _documentId,
            _maxRowsPerBatch,
            _originalDescriptorId,
            _replacementDescriptorId,
            _addressesBefore,
            _addressesAfter
        );

    [Test]
    public void It_partitions_collection_update_commands_using_the_compiled_batch_limit()
    {
        MultiBatchCollectionsIntegrationTestSupport
            .SchoolAddressInsertParameterCounts(_commandRecorder)
            .Should()
            .BeEmpty("a pure changed-descriptor update inserts no SchoolAddress rows");
        MultiBatchCollectionsIntegrationTestSupport
            .SchoolAddressDeleteParameterCounts(_commandRecorder)
            .Should()
            .BeEmpty("a pure changed-descriptor update deletes no SchoolAddress rows");

        NoProfileMultiBatchCollectionScenarios.AssertUpdateBatchPartitions(
            MultiBatchCollectionsIntegrationTestSupport.SchoolAddressUpdateParameterCounts(_commandRecorder),
            _maxRowsPerBatch,
            _parametersPerRow
        );
    }

    private async Task ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteMultiBatchCollectionChangedDescriptorUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        var createResult = await repository.UpsertDocument(
            MultiBatchCollectionsIntegrationTestSupport.CreateCreateRequestWithDescriptor(
                _mappingSet,
                MultiBatchCollectionsIntegrationTestSupport.CreateAddressRequestBodyWithDescriptor(
                    _createdAddressCount,
                    MultiBatchCollectionsIntegrationTestSupport.OriginalAddressTypeDescriptorUri
                ),
                _createdAddressCount,
                MultiBatchCollectionsIntegrationTestSupport.OriginalAddressTypeDescriptorUri,
                SchoolDocumentUuid,
                "pg-multi-batch-collection-changed-descriptor-create"
            )
        );

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
                    Name: "PostgresqlRelationalWriteMultiBatchCollectionChangedDescriptorUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MultiBatchCollectionsIntegrationTestSupport.CreateUpdateRequestWithDescriptor(
                _mappingSet,
                MultiBatchCollectionsIntegrationTestSupport.CreateAddressRequestBodyWithDescriptor(
                    _createdAddressCount,
                    MultiBatchCollectionsIntegrationTestSupport.ReplacementAddressTypeDescriptorUri
                ),
                _createdAddressCount,
                MultiBatchCollectionsIntegrationTestSupport.ReplacementAddressTypeDescriptorUri,
                SchoolDocumentUuid,
                "pg-multi-batch-collection-changed-descriptor-update"
            )
        );
    }
}
