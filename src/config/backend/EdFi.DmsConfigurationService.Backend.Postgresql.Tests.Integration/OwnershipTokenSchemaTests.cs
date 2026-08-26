// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class OwnershipTokenSchemaTests : DatabaseTest
{
    [Test]
    public async Task It_creates_the_ownership_token_catalog()
    {
        int count = await Connection!.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM information_schema.columns
            WHERE table_schema = 'dmscs'
              AND table_name = 'OwnershipToken'
              AND column_name IN ('Id', 'Description', 'CreatedAt', 'CreatedBy', 'LastModifiedAt', 'ModifiedBy', 'TenantId');
            """
        );

        count.Should().Be(7);
    }

    [Test]
    public async Task It_adds_creator_ownership_token_to_api_client()
    {
        int count = await Connection!.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM information_schema.columns
            WHERE table_schema = 'dmscs'
              AND table_name = 'ApiClient'
              AND column_name = 'CreatorOwnershipTokenId';
            """
        );

        count.Should().Be(1);
    }

    [Test]
    public async Task It_creates_the_assignment_composite_key()
    {
        int keyColumnCount = await Connection!.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM information_schema.key_column_usage
            WHERE table_schema = 'dmscs'
              AND table_name = 'ApiClientOwnershipToken'
              AND constraint_name = 'PK_ApiClientOwnershipToken'
              AND column_name IN ('ApiClientId', 'OwnershipTokenId');
            """
        );

        keyColumnCount.Should().Be(2);
    }

    [Test]
    public async Task It_restricts_tenant_deletion_when_ownership_tokens_reference_the_tenant()
    {
        string? deleteRule = await Connection!.ExecuteScalarAsync<string>(
            """
            SELECT constraint_info.confdeltype::text
            FROM pg_constraint constraint_info
            JOIN pg_class table_info
                ON table_info.oid = constraint_info.conrelid
            JOIN pg_namespace schema_info
                ON schema_info.oid = table_info.relnamespace
            WHERE schema_info.nspname = 'dmscs'
              AND table_info.relname = 'OwnershipToken'
              AND constraint_info.conname = 'FK_OwnershipToken_Tenant';
            """
        );

        deleteRule.Should().Be("r");
    }

    [Test]
    public async Task It_rejects_a_null_ownership_token_description()
    {
        Func<Task> act = () =>
            Connection!.ExecuteAsync(
                """
                INSERT INTO "dmscs"."OwnershipToken" ("Description")
                VALUES (@Description);
                """,
                new { Description = (string?)null }
            );

        await act.Should().ThrowAsync<PostgresException>();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t\r\n")]
    public async Task It_rejects_an_ownership_token_description_with_no_non_whitespace_characters(
        string description
    )
    {
        Func<Task> act = () =>
            Connection!.ExecuteAsync(
                """
                INSERT INTO "dmscs"."OwnershipToken" ("Description")
                VALUES (@Description);
                """,
                new { Description = description }
            );

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Test]
    public async Task It_accepts_a_50_character_ownership_token_description()
    {
        string description = new('A', 50);

        int count = await Connection!.ExecuteAsync(
            """
            INSERT INTO "dmscs"."OwnershipToken" ("Description")
            VALUES (@Description);
            """,
            new { Description = description }
        );

        count.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_a_blank_ownership_token_description_on_update()
    {
        short id = await Connection!.ExecuteScalarAsync<short>(
            """
            INSERT INTO "dmscs"."OwnershipToken" ("Description")
            VALUES ('Valid description')
            RETURNING "Id";
            """
        );

        Func<Task> act = () =>
            Connection!.ExecuteAsync(
                """
                UPDATE "dmscs"."OwnershipToken"
                SET "Description" = @Description
                WHERE "Id" = @Id;
                """,
                new { Id = id, Description = "   " }
            );

        await act.Should().ThrowAsync<PostgresException>();
    }
}
