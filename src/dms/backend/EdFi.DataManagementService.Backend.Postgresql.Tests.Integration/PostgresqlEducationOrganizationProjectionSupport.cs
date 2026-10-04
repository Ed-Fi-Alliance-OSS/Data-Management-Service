// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// A stored education organization's identifier and document id, which a reference to it stores.
/// </summary>
internal sealed record ProjectionReference(long DocumentId, long Id);

/// <summary>
/// Inserts education organizations directly into a provisioned PostgreSQL database.
/// </summary>
internal sealed class PostgresqlProjectionSeed
{
    private readonly PostgresqlGeneratedDdlTestDatabase _database;
    private short _stateEducationAgencyKey;
    private short _educationServiceCenterKey;
    private short _localEducationAgencyKey;
    private short _schoolKey;
    private short _postSecondaryInstitutionKey;
    private long _localEducationAgencyCategoryDescriptorId;

    private PostgresqlProjectionSeed(PostgresqlGeneratedDdlTestDatabase database)
    {
        _database = database;
    }

    public static async Task<PostgresqlProjectionSeed> CreateAsync(
        PostgresqlGeneratedDdlTestDatabase database
    )
    {
        var seed = new PostgresqlProjectionSeed(database);
        seed._stateEducationAgencyKey = await seed.ResourceKeyAsync("StateEducationAgency");
        seed._educationServiceCenterKey = await seed.ResourceKeyAsync("EducationServiceCenter");
        seed._localEducationAgencyKey = await seed.ResourceKeyAsync("LocalEducationAgency");
        seed._schoolKey = await seed.ResourceKeyAsync("School");
        seed._postSecondaryInstitutionKey = await seed.ResourceKeyAsync("PostSecondaryInstitution");

        short categoryKey = await seed.ResourceKeyAsync("LocalEducationAgencyCategoryDescriptor");
        seed._localEducationAgencyCategoryDescriptorId = await seed.DocumentAsync(categoryKey);
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue",
                "ShortDescription", "Description", "Discriminator", "Uri")
            VALUES (@documentId, @resourceKeyId, 'uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor',
                'Independent', 'Independent', 'Independent', 'Ed-Fi:LocalEducationAgencyCategoryDescriptor',
                'uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent');
            """,
            new NpgsqlParameter("documentId", seed._localEducationAgencyCategoryDescriptorId),
            new NpgsqlParameter("resourceKeyId", categoryKey)
        );

        return seed;
    }

    public async Task<ProjectionReference> StateEducationAgencyAsync(
        long id,
        string name,
        string? shortName = null
    )
    {
        long documentId = await DocumentAsync(_stateEducationAgencyKey);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StateEducationAgency" ("DocumentId", "StateEducationAgencyId",
                "NameOfInstitution", "ShortNameOfInstitution")
            VALUES (@documentId, @id, @name, @shortName);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("name", name),
            new NpgsqlParameter("shortName", (object?)shortName ?? DBNull.Value)
        );
        return new(documentId, id);
    }

    public async Task<ProjectionReference> EducationServiceCenterAsync(
        long id,
        string name,
        ProjectionReference? stateEducationAgency = null,
        string? shortName = null
    )
    {
        long documentId = await DocumentAsync(_educationServiceCenterKey);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."EducationServiceCenter" ("DocumentId", "EducationServiceCenterId",
                "NameOfInstitution", "ShortNameOfInstitution",
                "StateEducationAgency_DocumentId", "StateEducationAgency_StateEducationAgencyId")
            VALUES (@documentId, @id, @name, @shortName, @seaDocumentId, @seaId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("name", name),
            new NpgsqlParameter("shortName", (object?)shortName ?? DBNull.Value),
            new NpgsqlParameter("seaDocumentId", (object?)stateEducationAgency?.DocumentId ?? DBNull.Value),
            new NpgsqlParameter("seaId", (object?)stateEducationAgency?.Id ?? DBNull.Value)
        );
        return new(documentId, id);
    }

    public async Task<ProjectionReference> LocalEducationAgencyAsync(
        long id,
        string name,
        ProjectionReference? parentLocalEducationAgency = null,
        ProjectionReference? educationServiceCenter = null,
        ProjectionReference? stateEducationAgency = null,
        string? shortName = null
    )
    {
        long documentId = await DocumentAsync(_localEducationAgencyKey);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."LocalEducationAgency" ("DocumentId", "LocalEducationAgencyId",
                "LocalEducationAgencyCategoryDescriptor_DescriptorId", "NameOfInstitution", "ShortNameOfInstitution",
                "ParentLocalEducationAgency_DocumentId", "ParentLocalEducationAgency_LocalEducationAgencyId",
                "EducationServiceCenter_DocumentId", "EducationServiceCenter_EducationServiceCenterId",
                "StateEducationAgency_DocumentId", "StateEducationAgency_StateEducationAgencyId")
            VALUES (@documentId, @id, @category, @name, @shortName, @parentDocumentId, @parentId,
                @escDocumentId, @escId, @seaDocumentId, @seaId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("category", _localEducationAgencyCategoryDescriptorId),
            new NpgsqlParameter("name", name),
            new NpgsqlParameter("shortName", (object?)shortName ?? DBNull.Value),
            new NpgsqlParameter(
                "parentDocumentId",
                (object?)parentLocalEducationAgency?.DocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter("parentId", (object?)parentLocalEducationAgency?.Id ?? DBNull.Value),
            new NpgsqlParameter("escDocumentId", (object?)educationServiceCenter?.DocumentId ?? DBNull.Value),
            new NpgsqlParameter("escId", (object?)educationServiceCenter?.Id ?? DBNull.Value),
            new NpgsqlParameter("seaDocumentId", (object?)stateEducationAgency?.DocumentId ?? DBNull.Value),
            new NpgsqlParameter("seaId", (object?)stateEducationAgency?.Id ?? DBNull.Value)
        );
        return new(documentId, id);
    }

    public async Task<ProjectionReference> SchoolAsync(
        long id,
        string name,
        ProjectionReference? localEducationAgency = null,
        string? shortName = null
    )
    {
        long documentId = await DocumentAsync(_schoolKey);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "SchoolId", "NameOfInstitution", "ShortNameOfInstitution",
                "LocalEducationAgency_DocumentId", "LocalEducationAgency_LocalEducationAgencyId")
            VALUES (@documentId, @id, @name, @shortName, @leaDocumentId, @leaId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("name", name),
            new NpgsqlParameter("shortName", (object?)shortName ?? DBNull.Value),
            new NpgsqlParameter("leaDocumentId", (object?)localEducationAgency?.DocumentId ?? DBNull.Value),
            new NpgsqlParameter("leaId", (object?)localEducationAgency?.Id ?? DBNull.Value)
        );
        return new(documentId, id);
    }

    public async Task<ProjectionReference> PostSecondaryInstitutionAsync(long id, string name)
    {
        long documentId = await DocumentAsync(_postSecondaryInstitutionKey);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."PostSecondaryInstitution" ("DocumentId", "PostSecondaryInstitutionId", "NameOfInstitution")
            VALUES (@documentId, @id, @name);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("id", id),
            new NpgsqlParameter("name", name)
        );
        return new(documentId, id);
    }

    /// <summary>
    /// Seeds <see cref="ProjectionStandardHierarchy"/> and returns the rows a read must return.
    /// </summary>
    public async Task<IReadOnlyList<EducationOrganizationProjectionRow>> StandardHierarchyAsync()
    {
        var sea = await StateEducationAgencyAsync(1, "State Agency", "SEA");
        await StateEducationAgencyAsync(long.MinValue, "Lowest Agency");
        var esc = await EducationServiceCenterAsync(10, "Region 10", sea);
        var lea100 = await LocalEducationAgencyAsync(
            100,
            "District 100",
            educationServiceCenter: esc,
            stateEducationAgency: sea
        );
        var lea101 = await LocalEducationAgencyAsync(
            101,
            "District 101",
            parentLocalEducationAgency: lea100,
            educationServiceCenter: esc,
            stateEducationAgency: sea,
            shortName: ""
        );
        await SchoolAsync(100001, "School A", lea100, "SA");
        await SchoolAsync(101001, "School B", lea101);
        await SchoolAsync(900001, "Lone School");
        await SchoolAsync(9007199254740993, "Large Id School", lea101);
        await PostSecondaryInstitutionAsync(555, "College");

        return ProjectionStandardHierarchy.ExpectedRows;
    }

    /// <summary>
    /// Seeds exactly <paramref name="coreRows"/> core rows for measurement: one state agency, ten
    /// service centers, <paramref name="localEducationAgencies"/> local agencies, and schools for
    /// the rest, each school under a local agency. Inserted set-based; triggers fire per row.
    /// </summary>
    public async Task BulkHierarchyAsync(int coreRows, int localEducationAgencies)
    {
        var sea = await StateEducationAgencyAsync(1, "Measurement State Agency", "MSEA");
        for (int index = 1; index <= 10; index++)
        {
            await EducationServiceCenterAsync(10 + index, $"Measurement Region {index}", sea, $"MR{index}");
        }

        int schools = coreRows - 11 - localEducationAgencies;
        _database.CommandTimeoutSeconds = 1800;

        await _database.ExecuteNonQueryAsync(
            """
            WITH d AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT gen_random_uuid(), @key FROM generate_series(1, @count)
                RETURNING "DocumentId"),
            n AS (SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS i FROM d),
            esc AS (SELECT "DocumentId", "EducationServiceCenterId", row_number() OVER (ORDER BY "EducationServiceCenterId") - 1 AS j
                    FROM "edfi"."EducationServiceCenter")
            INSERT INTO "edfi"."LocalEducationAgency" ("DocumentId", "LocalEducationAgencyId",
                "LocalEducationAgencyCategoryDescriptor_DescriptorId", "NameOfInstitution", "ShortNameOfInstitution",
                "EducationServiceCenter_DocumentId", "EducationServiceCenter_EducationServiceCenterId",
                "StateEducationAgency_DocumentId", "StateEducationAgency_StateEducationAgencyId")
            SELECT n."DocumentId", 200000 + n.i, @category, 'Measurement District Number ' || n.i, 'MD' || n.i,
                esc."DocumentId", esc."EducationServiceCenterId", @seaDocumentId, 1
            FROM n JOIN esc ON esc.j = n.i % 10;
            """,
            new NpgsqlParameter("key", _localEducationAgencyKey),
            new NpgsqlParameter("count", localEducationAgencies),
            new NpgsqlParameter("category", _localEducationAgencyCategoryDescriptorId),
            new NpgsqlParameter("seaDocumentId", sea.DocumentId)
        );

        await _database.ExecuteNonQueryAsync(
            """
            WITH d AS (
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT gen_random_uuid(), @key FROM generate_series(1, @count)
                RETURNING "DocumentId"),
            n AS (SELECT "DocumentId", row_number() OVER (ORDER BY "DocumentId") AS i FROM d),
            l AS (SELECT "DocumentId", "LocalEducationAgencyId",
                         row_number() OVER (ORDER BY "LocalEducationAgencyId") - 1 AS j
                  FROM "edfi"."LocalEducationAgency")
            INSERT INTO "edfi"."School" ("DocumentId", "SchoolId", "NameOfInstitution", "ShortNameOfInstitution",
                "LocalEducationAgency_DocumentId", "LocalEducationAgency_LocalEducationAgencyId")
            SELECT n."DocumentId", 1000000 + n.i, 'Measurement School Number ' || n.i, 'MS' || n.i,
                l."DocumentId", l."LocalEducationAgencyId"
            FROM n JOIN l ON l.j = n.i % @leas;
            """,
            new NpgsqlParameter("key", _schoolKey),
            new NpgsqlParameter("count", schools),
            new NpgsqlParameter("leas", localEducationAgencies)
        );
    }

    private async Task<short> ResourceKeyAsync(string resourceName) =>
        await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId" FROM "dms"."ResourceKey"
            WHERE "ProjectName" = 'Ed-Fi' AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("resourceName", resourceName)
        );

    private async Task<long> DocumentAsync(short resourceKeyId) =>
        await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
}

/// <summary>
/// The hierarchy <see cref="PostgresqlProjectionSeed.StandardHierarchyAsync"/> stores, as read.
/// </summary>
internal static class ProjectionStandardHierarchy
{
    public static IReadOnlyList<EducationOrganizationProjectionRow> ExpectedRows { get; } =
    [
        new(long.MinValue, "Ed-Fi:StateEducationAgency", "Lowest Agency", null, null, null, null, null),
        new(1, "Ed-Fi:StateEducationAgency", "State Agency", "SEA", null, null, null, null),
        new(10, "Ed-Fi:EducationServiceCenter", "Region 10", null, null, null, null, 1),
        new(100, "Ed-Fi:LocalEducationAgency", "District 100", null, null, null, 10, 1),
        // Every reference slot is kept, including the two parent precedence will not select.
        new(101, "Ed-Fi:LocalEducationAgency", "District 101", "", null, 100, 10, 1),
        new(100001, "Ed-Fi:School", "School A", "SA", 100, null, null, null),
        new(101001, "Ed-Fi:School", "School B", null, 101, null, null, null),
        new(900001, "Ed-Fi:School", "Lone School", null, null, null, null, null),
        new(9007199254740993, "Ed-Fi:School", "Large Id School", null, 101, null, null, null),
    ];
}

/// <summary>
/// Records the read's database interaction and runs a test's interventions between stages.
/// </summary>
internal sealed class RecordingProjectionReadObserver : IEducationOrganizationProjectionReadObserver
{
    private readonly List<(string CommandText, bool Attached)> _commands = [];

    public Func<DbConnection, CancellationToken, Task>? OnAfterAcquire { get; init; }

    public Func<DbConnection, CancellationToken, Task>? OnBeforeCommit { get; init; }

    public IReadOnlyList<(string CommandText, bool Attached)> Commands => _commands;

    public bool? ReaderClosedBeforeCommit { get; private set; }

    public Task AfterAcquireAsync(DbConnection connection, CancellationToken cancellationToken) =>
        OnAfterAcquire?.Invoke(connection, cancellationToken) ?? Task.CompletedTask;

    public void CommandCreated(DbCommand command, DbTransaction transaction) =>
        _commands.Add((command.CommandText, ReferenceEquals(command.Transaction, transaction)));

    public Task BeforeCommitAsync(
        DbConnection connection,
        bool readerClosed,
        CancellationToken cancellationToken
    )
    {
        ReaderClosedBeforeCommit = readerClosed;
        return OnBeforeCommit?.Invoke(connection, cancellationToken) ?? Task.CompletedTask;
    }
}

internal static class PostgresqlProjectionReaders
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    /// <summary>A value that must never reach a log record.</summary>
    public const string HostileValue = "Hostile-Secret-9f2c";

    public static PostgresqlEducationOrganizationProjectionSetReader Create(
        string connectionString,
        RecordingLogger<PostgresqlEducationOrganizationProjectionSetReader> logger,
        IEducationOrganizationProjectionReadObserver? observer = null
    )
    {
        return new(
            async cancellationToken =>
            {
                var connection = new NpgsqlConnection(connectionString);
                try
                {
                    await connection.OpenAsync(cancellationToken);
                    return connection;
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }
            },
            logger,
            observer
        );
    }

    public static EducationOrganizationProjectionSetReadRequest Request(
        MappingSet mappingSet,
        int maxProjectionRows = 50_000,
        int lockTimeoutSeconds = 5,
        int commandTimeoutSeconds = 60
    ) => new(mappingSet, maxProjectionRows, lockTimeoutSeconds, commandTimeoutSeconds);

    /// <summary>
    /// A connection string to the database with its own application name and no pooling, so the
    /// read's session can be found in <c>pg_stat_activity</c> and ends when the read releases it.
    /// </summary>
    public static string Tagged(string connectionString, string applicationName) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
        }.ConnectionString;

    /// <summary>
    /// Asserts that no record carries an exception object or any of the forbidden values, and that
    /// every record is at most a warning.
    /// </summary>
    public static void AssertRedacted<T>(RecordingLogger<T> logger, params string[] forbidden)
    {
        foreach (var record in logger.Records)
        {
            record.Exception.Should().BeNull();
            record
                .Level.Should()
                .BeOneOf(LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning);
            foreach (var value in forbidden.Append(HostileValue).Append("edfi").Append("SELECT"))
            {
                record.Message.Should().NotContainEquivalentOf(value);
            }
        }
    }

    /// <summary>
    /// Waits for a state the database reports, failing after a bound far longer than the state takes.
    /// </summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {description}.");
            }

            await Task.Delay(25);
        }
    }

    public static async Task<long> CountAsync(string connectionString, string sql, string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("app", applicationName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
