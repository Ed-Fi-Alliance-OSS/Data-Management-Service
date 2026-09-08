// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_TokenInfo_EducationOrganization_Lookup
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private RecordingPostgresqlRelationalCommandExecutor _commandExecutor = null!;
    private short _stateEducationAgencyResourceKeyId;
    private short _localEducationAgencyResourceKeyId;
    private short _schoolResourceKeyId;
    private short _localEducationAgencyCategoryDescriptorResourceKeyId;
    private long _localEducationAgencyCategoryDescriptorDocumentId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        _stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "StateEducationAgency");
        _localEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "LocalEducationAgency");
        _schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        _localEducationAgencyCategoryDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "LocalEducationAgencyCategoryDescriptor"
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _commandExecutor = new RecordingPostgresqlRelationalCommandExecutor(_database.ConnectionString);

        _localEducationAgencyCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            documentUuid: Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa"),
            resourceKeyId: _localEducationAgencyCategoryDescriptorResourceKeyId,
            discriminator: "Ed-Fi:LocalEducationAgencyCategoryDescriptor",
            uri: "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent",
            @namespace: "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor",
            codeValue: "Independent",
            shortDescription: "Independent"
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_returns_accessible_targets_and_ancestors_from_the_relational_auth_table()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var leaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertSchoolAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000700"),
            schoolId: 700,
            nameOfInstitution: "Test School",
            parentLocalEducationAgencyDocumentId: leaDocumentId,
            parentLocalEducationAgencyId: 500
        );

        var rows = await ExecuteLookupAsync([100, 999999]);

        rows.Should()
            .Equal(
                new TokenInfoEducationOrganization(
                    100,
                    "Test SEA",
                    "Ed-Fi:StateEducationAgency",
                    "Ed-Fi:StateEducationAgency",
                    100
                ),
                new TokenInfoEducationOrganization(
                    500,
                    "Test LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:StateEducationAgency",
                    100
                ),
                new TokenInfoEducationOrganization(
                    500,
                    "Test LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:LocalEducationAgency",
                    500
                ),
                new TokenInfoEducationOrganization(
                    700,
                    "Test School",
                    "Ed-Fi:School",
                    "Ed-Fi:StateEducationAgency",
                    100
                ),
                new TokenInfoEducationOrganization(
                    700,
                    "Test School",
                    "Ed-Fi:School",
                    "Ed-Fi:LocalEducationAgency",
                    500
                ),
                new TokenInfoEducationOrganization(700, "Test School", "Ed-Fi:School", "Ed-Fi:School", 700)
            );

        _commandExecutor.Commands.Should().ContainSingle();
        _commandExecutor
            .Commands[0]
            .CommandText.Should()
            .Contain("""FROM "auth"."EducationOrganizationIdToEducationOrganizationId" h""");
        _commandExecutor.Commands[0].CommandText.Should().NotContain("EducationOrganizationHierarchy");
    }

    [Test]
    public async Task It_returns_empty_for_empty_or_fully_unresolved_claims_without_a_server_error()
    {
        var emptyRows = await ExecuteLookupAsync([]);
        emptyRows.Should().BeEmpty();
        _commandExecutor.Commands.Should().BeEmpty();

        var unresolvedRows = await ExecuteLookupAsync([987654]);
        unresolvedRows.Should().BeEmpty();
        _commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_adds_direct_self_rows_and_deduplicates_existing_auth_self_rows()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000900"),
            localEducationAgencyId: 900,
            nameOfInstitution: "Orphan LEA"
        );
        await DeleteAuthTupleAsync(900, 900);

        var rows = await ExecuteLookupAsync([500, 900]);

        rows.Should()
            .Equal(
                new TokenInfoEducationOrganization(
                    500,
                    "Test LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:StateEducationAgency",
                    100
                ),
                new TokenInfoEducationOrganization(
                    500,
                    "Test LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:LocalEducationAgency",
                    500
                ),
                new TokenInfoEducationOrganization(
                    900,
                    "Orphan LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:LocalEducationAgency",
                    900
                )
            );
    }

    [Test]
    public async Task It_ignores_stale_auth_tuples_with_missing_target_or_ancestor_projection()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertAuthTupleAsync(100, 999);
        await InsertAuthTupleAsync(888, 500);

        var rows = await ExecuteLookupAsync([100]);

        rows.Should()
            .Equal(
                new TokenInfoEducationOrganization(
                    100,
                    "Test SEA",
                    "Ed-Fi:StateEducationAgency",
                    "Ed-Fi:StateEducationAgency",
                    100
                ),
                new TokenInfoEducationOrganization(
                    500,
                    "Test LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:StateEducationAgency",
                    100
                ),
                new TokenInfoEducationOrganization(
                    500,
                    "Test LEA",
                    "Ed-Fi:LocalEducationAgency",
                    "Ed-Fi:LocalEducationAgency",
                    500
                )
            );
        rows.Select(static row => row.EducationOrganizationId).Should().NotContain(999);
        rows.Select(static row => row.AncestorEducationOrganizationId).Should().NotContain(888);
    }

    private async Task<IReadOnlyList<TokenInfoEducationOrganization>> ExecuteLookupAsync(
        IReadOnlyCollection<long> educationOrganizationIds
    )
    {
        var lookup = new PostgresqlTokenInfoEducationOrganizationLookup(_commandExecutor);
        var rows = await lookup.GetEducationOrganizations(
            [.. educationOrganizationIds.Select(static value => new EducationOrganizationId(value))],
            _fixture.MappingSet
        );

        return [.. rows];
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task<long> InsertStateEducationAgencyAsync(
        Guid documentUuid,
        long stateEducationAgencyId,
        string nameOfInstitution
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _stateEducationAgencyResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StateEducationAgency" (
                "DocumentId",
                "StateEducationAgencyId",
                "NameOfInstitution"
            )
            VALUES (@documentId, @stateEducationAgencyId, @nameOfInstitution);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("stateEducationAgencyId", stateEducationAgencyId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution)
        );

        return documentId;
    }

    private async Task<long> InsertLocalEducationAgencyAsync(
        Guid documentUuid,
        long localEducationAgencyId,
        string nameOfInstitution,
        long? parentStateEducationAgencyDocumentId = null,
        long? parentStateEducationAgencyId = null
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _localEducationAgencyResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."LocalEducationAgency" (
                "DocumentId",
                "LocalEducationAgencyId",
                "LocalEducationAgencyCategoryDescriptor_DescriptorId",
                "NameOfInstitution",
                "StateEducationAgency_DocumentId",
                "StateEducationAgency_StateEducationAgencyId"
            )
            VALUES (
                @documentId,
                @localEducationAgencyId,
                @categoryDescriptorDocumentId,
                @nameOfInstitution,
                @parentSeaDocumentId,
                @parentSeaId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("localEducationAgencyId", localEducationAgencyId),
            new NpgsqlParameter(
                "categoryDescriptorDocumentId",
                _localEducationAgencyCategoryDescriptorDocumentId
            ),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter(
                "parentSeaDocumentId",
                (object?)parentStateEducationAgencyDocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter("parentSeaId", (object?)parentStateEducationAgencyId ?? DBNull.Value)
        );

        return documentId;
    }

    private async Task<long> InsertSchoolAsync(
        Guid documentUuid,
        long schoolId,
        string nameOfInstitution,
        long parentLocalEducationAgencyDocumentId,
        long parentLocalEducationAgencyId
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _schoolResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" (
                "DocumentId",
                "SchoolId",
                "NameOfInstitution",
                "LocalEducationAgency_DocumentId",
                "LocalEducationAgency_LocalEducationAgencyId"
            )
            VALUES (
                @documentId,
                @schoolId,
                @nameOfInstitution,
                @parentLeaDocumentId,
                @parentLeaId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("parentLeaDocumentId", parentLocalEducationAgencyDocumentId),
            new NpgsqlParameter("parentLeaId", parentLocalEducationAgencyId)
        );

        return documentId;
    }

    private async Task InsertAuthTupleAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" (
                "SourceEducationOrganizationId",
                "TargetEducationOrganizationId"
            )
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new NpgsqlParameter("sourceEducationOrganizationId", sourceEducationOrganizationId),
            new NpgsqlParameter("targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    private async Task DeleteAuthTupleAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
            WHERE "SourceEducationOrganizationId" = @sourceEducationOrganizationId
              AND "TargetEducationOrganizationId" = @targetEducationOrganizationId;
            """,
            new NpgsqlParameter("sourceEducationOrganizationId", sourceEducationOrganizationId),
            new NpgsqlParameter("targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    private sealed class RecordingPostgresqlRelationalCommandExecutor(string connectionString)
        : IRelationalCommandExecutor
    {
        private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string is required.", nameof(connectionString));
        private readonly List<RelationalCommand> _commands = [];

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public IReadOnlyList<RelationalCommand> Commands => _commands;

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(readAsync);

            _commands.Add(command);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = command.CommandText;

            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                parameter.ConfigureParameter?.Invoke(dbParameter);
                dbCommand.Parameters.Add(dbParameter);
            }

            await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);
            await using var relationalReader = new RecordingPostgresqlRelationalCommandReader(reader);

            return await readAsync(relationalReader, cancellationToken);
        }
    }

    private sealed class RecordingPostgresqlRelationalCommandReader(DbDataReader reader)
        : IRelationalCommandReader
    {
        private readonly DbDataReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

        public ValueTask DisposeAsync()
        {
            return _reader.DisposeAsync();
        }

        public Task<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            return _reader.ReadAsync(cancellationToken);
        }

        public Task<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            return _reader.NextResultAsync(cancellationToken);
        }

        public int GetOrdinal(string name)
        {
            return _reader.GetOrdinal(name);
        }

        public T GetFieldValue<T>(int ordinal)
        {
            return _reader.GetFieldValue<T>(ordinal);
        }

        public bool IsDBNull(int ordinal)
        {
            return _reader.IsDBNull(ordinal);
        }
    }
}
