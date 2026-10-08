// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// What the SQL Server script prologue is for, exercised from the one client shape it exists for: a
/// session whose <c>QUOTED_IDENTIFIER</c> is OFF, which is what ODBC <c>sqlcmd</c> opens without
/// <c>-I</c>. Every other fixture applies generated DDL over <c>Microsoft.Data.SqlClient</c>, which
/// already defaults the option ON, so nothing else here can tell a script with the prologue from one
/// without it.
/// </summary>
/// <remarks>
/// The scratch table and the dialect-rendered statements stand in for the generated script: the
/// prologue is the dialect's own text minus its <c>GO</c>, because <c>SqlClient</c> does not accept
/// batch separators, and the index and trigger are rendered the way the emitters render them. Each
/// test opens its own connection so one session's SET state cannot leak into another.
/// </remarks>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("ScriptPrologue")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Script_Prologue_Applied_By_A_Client_With_Quoted_Identifier_Off
{
    private const int SetOptionsIncorrectErrorNumber = 1934;

    private static readonly MssqlDialect _dialect = new(new MssqlDialectRules());
    private static readonly DbTableName _scratchTable = new(new DbSchemaName("dbo"), "PrologueScratch");
    private static readonly DbColumnName _tokenColumn = new("Token");

    private MssqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await _database.ExecuteNonQueryAsync(
            """
            CREATE TABLE [dbo].[PrologueScratch] ([Id] int NOT NULL PRIMARY KEY, [Token] smallint NULL);
            CREATE TABLE [dbo].[PrologueScratchChild] ([Id] int NOT NULL PRIMARY KEY, [ScratchId] int NOT NULL);
            """
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

    /// <summary>
    /// The failure the prologue prevents, so the passing case below is evidence about the prologue
    /// rather than about the client: without it, the session's OFF setting makes SQL Server refuse the
    /// filtered index.
    /// </summary>
    [Test]
    public async Task It_cannot_create_the_filtered_index_without_the_prologue()
    {
        await using SqlConnection connection = await OpenWithQuotedIdentifierOffAsync();

        var create = () =>
            ExecuteAsync(connection, FilteredIndexStatement("IX_PrologueScratch_Token_WithoutPrologue"));

        (await create.Should().ThrowAsync<SqlException>())
            .Which.Number.Should()
            .Be(
                SetOptionsIncorrectErrorNumber,
                "a filtered index requires QUOTED_IDENTIFIER ON in the creating session, which this "
                    + "client does not have"
            );
    }

    /// <summary>
    /// The prologue puts the session right before the script's first object, so the filtered index the
    /// script depends on is created by a client whose defaults would otherwise have refused it.
    /// </summary>
    [Test]
    public async Task It_creates_the_filtered_index_when_the_prologue_runs_first()
    {
        await using SqlConnection connection = await OpenWithQuotedIdentifierOffAsync();

        await ExecuteAsync(connection, PrologueWithoutBatchSeparator());
        await ExecuteAsync(connection, FilteredIndexStatement("IX_PrologueScratch_Token"));

        bool hasFilter = await _database.ExecuteScalarAsync<bool>(
            "SELECT [has_filter] FROM sys.indexes WHERE [name] = N'IX_PrologueScratch_Token';"
        );

        hasFilter.Should().BeTrue("the index the prologue made creatable is the filtered one");
    }

    /// <summary>
    /// The half that outlives the session: SQL Server captures <c>QUOTED_IDENTIFIER</c> into each
    /// trigger at creation, and a stamp trigger baked OFF fails every later write to a table with a
    /// filtered index from inside the trigger, whatever the writing session's own setting. The prologue
    /// is what makes the captured setting right.
    /// </summary>
    [Test]
    public async Task It_bakes_quoted_identifier_on_into_a_trigger_created_after_it()
    {
        await using SqlConnection connection = await OpenWithQuotedIdentifierOffAsync();

        await ExecuteAsync(connection, PrologueWithoutBatchSeparator());
        await ExecuteAsync(
            connection,
            """
            CREATE OR ALTER TRIGGER [dbo].[TR_PrologueScratchChild_Stamp]
            ON [dbo].[PrologueScratchChild]
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE s SET s.[Token] = s.[Token]
                FROM [dbo].[PrologueScratch] s
                INNER JOIN inserted i ON s.[Id] = i.[ScratchId];
            END;
            """
        );

        bool usesQuotedIdentifier = await _database.ExecuteScalarAsync<bool>(
            """
            SELECT m.[uses_quoted_identifier]
            FROM sys.sql_modules m
            WHERE m.[object_id] = OBJECT_ID(N'[dbo].[TR_PrologueScratchChild_Stamp]');
            """
        );

        usesQuotedIdentifier
            .Should()
            .BeTrue(
                "the trigger's captured setting, not the writing session's, governs its writes to a "
                    + "table with a filtered index"
            );
    }

    private async Task<SqlConnection> OpenWithQuotedIdentifierOffAsync()
    {
        SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET QUOTED_IDENTIFIER OFF;");
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The dialect's prologue as one executable batch: everything ahead of its <c>GO</c>.</summary>
    private static string PrologueWithoutBatchSeparator()
    {
        string prologue = _dialect.RenderScriptPrologue();
        int batchSeparator = prologue.IndexOf("\nGO", StringComparison.Ordinal);

        batchSeparator.Should().BePositive("the prologue is a batch terminated by GO");

        return prologue[..batchSeparator];
    }

    private static string FilteredIndexStatement(string indexName) =>
        _dialect.CreateIndexIfNotExists(
            _scratchTable,
            indexName,
            [_tokenColumn],
            notNullFilterColumn: _tokenColumn
        );
}
