// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Sql_Server_Reciprocal_Identity_Cycle_With_One_Native_Cascade
{
    private static readonly Guid _cycleADocumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _cycleBDocumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private MssqlGeneratedDdlTestDatabase _database = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _cycleAForeignKeys = null!;
    private IReadOnlyList<MssqlForeignKeyMetadata> _cycleBForeignKeys = null!;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _constraintViolations = null!;
    private SqlException _directCycleAUpdateException = null!;
    private SqlException _twoCascadeCycleException = null!;
    private bool _key1OnlyMutationWasCorrelated;
    private bool _key2OnlyMutationWasCorrelated;
    private bool _allComponentsMutationWasCorrelated;

    [OneTimeSetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await _database.ExecuteNonQueryAsync(
            """
            CREATE TABLE [dbo].[CycleA]
            (
                [DocumentId] uniqueidentifier NOT NULL,
                [Key1] int NOT NULL,
                [Key2] int NOT NULL,
                [B_DocumentId] uniqueidentifier NOT NULL,
                CONSTRAINT [PK_CycleA] PRIMARY KEY ([DocumentId]),
                CONSTRAINT [UX_CycleA_PropagationKey] UNIQUE ([Key1], [Key2], [DocumentId])
            );

            CREATE TABLE [dbo].[CycleB]
            (
                [DocumentId] uniqueidentifier NOT NULL,
                [Key1] int NOT NULL,
                [Key2] int NOT NULL,
                [A_DocumentId] uniqueidentifier NULL,
                CONSTRAINT [PK_CycleB] PRIMARY KEY ([DocumentId]),
                CONSTRAINT [UX_CycleB_PropagationKey] UNIQUE ([Key1], [Key2], [DocumentId])
            );

            ALTER TABLE [dbo].[CycleA]
                ADD CONSTRAINT [FK_CycleA_CycleB]
                FOREIGN KEY ([Key1], [Key2], [B_DocumentId])
                REFERENCES [dbo].[CycleB] ([Key1], [Key2], [DocumentId])
                ON UPDATE CASCADE;

            ALTER TABLE [dbo].[CycleB]
                ADD CONSTRAINT [FK_CycleB_CycleA]
                FOREIGN KEY ([Key1], [Key2], [A_DocumentId])
                REFERENCES [dbo].[CycleA] ([Key1], [Key2], [DocumentId])
                ON UPDATE NO ACTION;
            """
        );

        _cycleAForeignKeys = await _database.GetForeignKeyMetadataAsync("dbo", "CycleA");
        _cycleBForeignKeys = await _database.GetForeignKeyMetadataAsync("dbo", "CycleB");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dbo].[CycleB] ([DocumentId], [Key1], [Key2], [A_DocumentId])
            VALUES (@cycleBDocumentId, 10, 20, NULL);

            INSERT INTO [dbo].[CycleA] ([DocumentId], [Key1], [Key2], [B_DocumentId])
            VALUES (@cycleADocumentId, 10, 20, @cycleBDocumentId);

            UPDATE [dbo].[CycleB]
            SET [A_DocumentId] = @cycleADocumentId
            WHERE [DocumentId] = @cycleBDocumentId;
            """,
            new SqlParameter("@cycleADocumentId", _cycleADocumentId),
            new SqlParameter("@cycleBDocumentId", _cycleBDocumentId)
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dbo].[CycleB]
            SET [Key1] = 11
            WHERE [DocumentId] = @cycleBDocumentId;
            """,
            new SqlParameter("@cycleBDocumentId", _cycleBDocumentId)
        );
        _key1OnlyMutationWasCorrelated = await CycleRowsAreCorrelatedAsync(11, 20);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dbo].[CycleB]
            SET [Key2] = 21
            WHERE [DocumentId] = @cycleBDocumentId;
            """,
            new SqlParameter("@cycleBDocumentId", _cycleBDocumentId)
        );
        _key2OnlyMutationWasCorrelated = await CycleRowsAreCorrelatedAsync(11, 21);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dbo].[CycleB]
            SET [Key1] = 12, [Key2] = 22
            WHERE [DocumentId] = @cycleBDocumentId;
            """,
            new SqlParameter("@cycleBDocumentId", _cycleBDocumentId)
        );
        _allComponentsMutationWasCorrelated = await CycleRowsAreCorrelatedAsync(12, 22);
        _constraintViolations = await _database.QueryRowsAsync("DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS;");

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                UPDATE [dbo].[CycleA]
                SET [Key1] = 99
                WHERE [DocumentId] = @cycleADocumentId;
                """,
                new SqlParameter("@cycleADocumentId", _cycleADocumentId)
            );
        }
        catch (SqlException exception)
        {
            _directCycleAUpdateException = exception;
        }

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                CREATE TABLE [dbo].[TwoCascadeA]
                (
                    [DocumentId] uniqueidentifier NOT NULL,
                    [Key1] int NOT NULL,
                    [Key2] int NOT NULL,
                    [B_DocumentId] uniqueidentifier NOT NULL,
                    CONSTRAINT [PK_TwoCascadeA] PRIMARY KEY ([DocumentId]),
                    CONSTRAINT [UX_TwoCascadeA_PropagationKey]
                        UNIQUE ([Key1], [Key2], [DocumentId])
                );

                CREATE TABLE [dbo].[TwoCascadeB]
                (
                    [DocumentId] uniqueidentifier NOT NULL,
                    [Key1] int NOT NULL,
                    [Key2] int NOT NULL,
                    [A_DocumentId] uniqueidentifier NULL,
                    CONSTRAINT [PK_TwoCascadeB] PRIMARY KEY ([DocumentId]),
                    CONSTRAINT [UX_TwoCascadeB_PropagationKey]
                        UNIQUE ([Key1], [Key2], [DocumentId])
                );

                ALTER TABLE [dbo].[TwoCascadeA]
                    ADD CONSTRAINT [FK_TwoCascadeA_TwoCascadeB]
                    FOREIGN KEY ([Key1], [Key2], [B_DocumentId])
                    REFERENCES [dbo].[TwoCascadeB] ([Key1], [Key2], [DocumentId])
                    ON UPDATE CASCADE;

                ALTER TABLE [dbo].[TwoCascadeB]
                    ADD CONSTRAINT [FK_TwoCascadeB_TwoCascadeA]
                    FOREIGN KEY ([Key1], [Key2], [A_DocumentId])
                    REFERENCES [dbo].[TwoCascadeA] ([Key1], [Key2], [DocumentId])
                    ON UPDATE CASCADE;
                """
            );
        }
        catch (SqlException exception)
        {
            _twoCascadeCycleException = exception;
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_installs_the_cycle_with_the_selected_update_actions()
    {
        _cycleAForeignKeys.Single().UpdateAction.Should().Be("CASCADE");
        _cycleBForeignKeys.Single().UpdateAction.Should().Be("NO ACTION");
    }

    [Test]
    public void It_propagates_each_supported_primitive_mutation_subset()
    {
        _key1OnlyMutationWasCorrelated.Should().BeTrue();
        _key2OnlyMutationWasCorrelated.Should().BeTrue();
        _allComponentsMutationWasCorrelated.Should().BeTrue();
    }

    [Test]
    public void It_leaves_both_full_foreign_keys_trusted_and_valid()
    {
        _constraintViolations.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_a_direct_update_from_the_non_authorized_cycle_origin()
    {
        _directCycleAUpdateException.Number.Should().Be(547);
    }

    [Test]
    public void It_reproduces_error_1785_when_both_cycle_edges_cascade()
    {
        _twoCascadeCycleException.Number.Should().Be(1785);
    }

    private Task<bool> CycleRowsAreCorrelatedAsync(int expectedKey1, int expectedKey2) =>
        _database.ExecuteScalarAsync<bool>(
            """
            SELECT CAST(
                CASE WHEN EXISTS
                (
                    SELECT 1
                    FROM [dbo].[CycleA] AS a
                    INNER JOIN [dbo].[CycleB] AS b
                        ON b.[DocumentId] = a.[B_DocumentId]
                       AND a.[DocumentId] = b.[A_DocumentId]
                    WHERE a.[DocumentId] = @cycleADocumentId
                      AND b.[DocumentId] = @cycleBDocumentId
                      AND a.[Key1] = @expectedKey1
                      AND b.[Key1] = @expectedKey1
                      AND a.[Key2] = @expectedKey2
                      AND b.[Key2] = @expectedKey2
                ) THEN 1 ELSE 0 END
                AS bit
            );
            """,
            new SqlParameter("@cycleADocumentId", _cycleADocumentId),
            new SqlParameter("@cycleBDocumentId", _cycleBDocumentId),
            new SqlParameter("@expectedKey1", expectedKey1),
            new SqlParameter("@expectedKey2", expectedKey2)
        );
}
