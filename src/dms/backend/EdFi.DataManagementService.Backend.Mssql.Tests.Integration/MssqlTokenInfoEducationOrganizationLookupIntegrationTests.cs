// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Relational_TokenInfo_EducationOrganization_Lookup
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private RecordingMssqlRelationalCommandExecutor _commandExecutor = null!;
    private short _stateEducationAgencyResourceKeyId;
    private short _localEducationAgencyResourceKeyId;
    private short _schoolResourceKeyId;
    private short _localEducationAgencyCategoryDescriptorResourceKeyId;
    private long _localEducationAgencyCategoryDescriptorDocumentId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;

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
        _commandExecutor = new RecordingMssqlRelationalCommandExecutor(_database.ConnectionString);

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
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
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
            .Contain("FROM [auth].[EducationOrganizationIdToEducationOrganizationId] h");
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

    [Test]
    public async Task It_executes_with_the_sql_server_structured_claim_parameter()
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

        var rows = await ExecuteLookupAsync([
            .. Enumerable.Range(1, 2000).Select(static value => (long)value),
        ]);

        rows.Should().HaveCount(6);
        _commandExecutor.Commands.Should().ContainSingle();
        _commandExecutor
            .Commands[0]
            .CommandText.Should()
            .Contain(
                "WHERE c.[EducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
        _commandExecutor.Commands[0].Parameters.Should().ContainSingle();
        _commandExecutor.Commands[0].Parameters[0].Value.Should().BeOfType<DataTable>();
    }

    private async Task<IReadOnlyList<TokenInfoEducationOrganization>> ExecuteLookupAsync(
        IReadOnlyCollection<long> educationOrganizationIds
    )
    {
        var lookup = new MssqlTokenInfoEducationOrganizationLookup(
            _commandExecutor,
            new TestMssqlRelationalParameterConfigurator()
        );
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
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT inserted.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [ResourceKeyId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
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
            INSERT INTO [edfi].[StateEducationAgency] (
                [DocumentId],
                [StateEducationAgencyId],
                [NameOfInstitution]
            )
            VALUES (@documentId, @stateEducationAgencyId, @nameOfInstitution);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@stateEducationAgencyId", stateEducationAgencyId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution)
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
            INSERT INTO [edfi].[LocalEducationAgency] (
                [DocumentId],
                [LocalEducationAgencyId],
                [LocalEducationAgencyCategoryDescriptor_DescriptorId],
                [NameOfInstitution],
                [StateEducationAgency_DocumentId],
                [StateEducationAgency_StateEducationAgencyId]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@localEducationAgencyId", localEducationAgencyId),
            new SqlParameter(
                "@categoryDescriptorDocumentId",
                _localEducationAgencyCategoryDescriptorDocumentId
            ),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter(
                "@parentSeaDocumentId",
                (object?)parentStateEducationAgencyDocumentId ?? DBNull.Value
            ),
            new SqlParameter("@parentSeaId", (object?)parentStateEducationAgencyId ?? DBNull.Value)
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
            INSERT INTO [edfi].[School] (
                [DocumentId],
                [SchoolId],
                [NameOfInstitution],
                [LocalEducationAgency_DocumentId],
                [LocalEducationAgency_LocalEducationAgencyId]
            )
            VALUES (
                @documentId,
                @schoolId,
                @nameOfInstitution,
                @parentLeaDocumentId,
                @parentLeaId
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@parentLeaDocumentId", parentLocalEducationAgencyDocumentId),
            new SqlParameter("@parentLeaId", parentLocalEducationAgencyId)
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
            INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] (
                [SourceEducationOrganizationId],
                [TargetEducationOrganizationId]
            )
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    private async Task DeleteAuthTupleAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
            WHERE [SourceEducationOrganizationId] = @sourceEducationOrganizationId
              AND [TargetEducationOrganizationId] = @targetEducationOrganizationId;
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    private sealed class RecordingMssqlRelationalCommandExecutor(string connectionString)
        : IRelationalCommandExecutor
    {
        private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string is required.", nameof(connectionString));
        private readonly List<RelationalCommand> _commands = [];

        public SqlDialect Dialect => SqlDialect.Mssql;

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

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = command.CommandText;
            dbCommand.CommandTimeout = 300;

            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                parameter.ConfigureParameter?.Invoke(dbParameter);
                dbCommand.Parameters.Add(dbParameter);
            }

            await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);
            await using var relationalReader = new RecordingMssqlRelationalCommandReader(reader);

            return await readAsync(relationalReader, cancellationToken);
        }
    }

    private sealed class RecordingMssqlRelationalCommandReader(DbDataReader reader) : IRelationalCommandReader
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

    private sealed class TestMssqlRelationalParameterConfigurator : IRelationalParameterConfigurator
    {
        public void ConfigureParameter(DbParameter dbParameter, QuerySqlParameter querySqlParameter)
        {
            ArgumentNullException.ThrowIfNull(dbParameter);
            ArgumentNullException.ThrowIfNull(querySqlParameter);

            if (querySqlParameter.Binding.Kind is not QuerySqlParameterBindingKind.MssqlStructured)
            {
                throw new NotSupportedException(
                    $"SQL Server test parameter configurator does not support binding kind '{querySqlParameter.Binding.Kind}'."
                );
            }

            if (dbParameter is not SqlParameter sqlParameter)
            {
                throw new InvalidOperationException(
                    "SQL Server structured token_info parameter binding requires a SqlParameter instance."
                );
            }

            sqlParameter.SqlDbType = SqlDbType.Structured;
            sqlParameter.TypeName =
                querySqlParameter.Binding.StructuredTypeName
                ?? throw new InvalidOperationException(
                    $"Structured binding for parameter '{querySqlParameter.ParameterName}' is missing a type name."
                );
        }
    }
}
