// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Exercises the emitted computed URI index on a provisioning session and on ordinary pooled
/// SqlClient sessions. Runtime SET options must work independently of the provisioning connection.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Descriptor_Uri_Index_On_Independent_Sessions
{
    private MssqlGeneratedDdlTestDatabase _database = default!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server descriptor URI index tests require a MssqlAdmin connection string"
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();

        // This pool is distinct from every runtime connection opened below.
        var builder = new SqlConnectionStringBuilder(_database.ConnectionString)
        {
            ApplicationName = "DescriptorUriIndexProvisioning",
        };
        await using SqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "SET QUOTED_IDENTIFIER OFF; SET ANSI_WARNINGS OFF; SET ARITHABORT OFF; SET NUMERIC_ROUNDABORT ON;"
        );

        var dialect = new MssqlDialect(new MssqlDialectRules());
        var sql = dialect.RenderScriptPrologue() + new CoreDdlEmitter(dialect).Emit();
        foreach (
            var batch in Regex
                .Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline)
                .Where(b => b.Trim().Length > 0)
        )
        {
            await ExecuteAsync(connection, batch);
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
            VALUES (1, N'Ed-Fi', N'SexDescriptor', N'5.2');
            INSERT INTO [dms].[DocumentCacheState] ([StateId], [ProjectionLifecycleState], [CacheAheadRecoveryRequired])
            VALUES (1, 'Disabled', 0);
            CREATE TABLE [dbo].[FormerDescriptorUri] ([Uri] nvarchar(306) NOT NULL);
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

    [Test]
    public async Task It_should_preserve_the_former_stored_uri_collation_without_persisting_the_computation()
    {
        var matchingColumns = await _database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.computed_columns c
            JOIN sys.columns former ON former.[object_id] = OBJECT_ID(N'dbo.FormerDescriptorUri')
                AND former.[name] = N'Uri'
            WHERE c.[object_id] = OBJECT_ID(N'dms.Descriptor') AND c.[name] = N'Uri'
                AND c.[is_persisted] = 0 AND c.[collation_name] = former.[collation_name]
                AND c.[max_length] = former.[max_length];
            """
        );
        matchingColumns.Should().Be(1);
    }

    [Test]
    public async Task It_should_maintain_the_computed_index_on_fresh_and_reused_runtime_connections()
    {
        // No SET statements: the runtime acquisition boundary opens SqlClient connections directly.
        // A separate pool, limited to one connection, proves reuse without inheriting provisioner state.
        var builder = new SqlConnectionStringBuilder(_database.ConnectionString)
        {
            ApplicationName = "DescriptorUriIndexRuntime",
            Pooling = true,
            MaxPoolSize = 1,
        };
        int firstProcessId;
        await using (SqlConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync();
            firstProcessId = connection.ServerProcessId;
            await AssertEffectiveIndexOptionsAsync(connection);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId]) VALUES (NEWID(), 1);
                DECLARE @documentId bigint = SCOPE_IDENTITY();
                INSERT INTO [dms].[Descriptor] ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription])
                VALUES (@documentId, 1, N'uri://ed-fi.org/SexDescriptor', N'Female', N'Female');
                """
            );
        }
        await using (SqlConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync();
            connection
                .ServerProcessId.Should()
                .Be(firstProcessId, "the runtime connection must come from its pool");
            await AssertEffectiveIndexOptionsAsync(connection);
            await ExecuteAsync(
                connection,
                """
                UPDATE [dms].[Descriptor] SET [CodeValue] = N'Male', [ShortDescription] = N'Male';
                DELETE FROM [dms].[Descriptor];
                DELETE FROM [dms].[Document];
                """
            );
        }
    }

    private static async Task AssertEffectiveIndexOptionsAsync(SqlConnection connection)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN SESSIONPROPERTY('ANSI_NULLS') = 1
                AND SESSIONPROPERTY('ANSI_PADDING') = 1
                AND SESSIONPROPERTY('ANSI_WARNINGS') = 1
                AND SESSIONPROPERTY('CONCAT_NULL_YIELDS_NULL') = 1
                AND SESSIONPROPERTY('QUOTED_IDENTIFIER') = 1
                AND SESSIONPROPERTY('NUMERIC_ROUNDABORT') = 0
                AND (SESSIONPROPERTY('ARITHABORT') = 1 OR
                    (SESSIONPROPERTY('ANSI_WARNINGS') = 1 AND
                        (SELECT [compatibility_level] FROM sys.databases WHERE [name] = DB_NAME()) >= 90))
                THEN 1 ELSE 0 END;
            """;
        var result =
            await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The SQL Server session option probe returned no result.");
        result.Should().Be(1, "ANSI_WARNINGS enables effective ARITHABORT at supported compatibility levels");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
