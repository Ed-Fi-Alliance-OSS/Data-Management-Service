// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class OwnershipTokenSchemaTests : DatabaseTest
{
    [Test]
    public async Task It_creates_the_ownership_token_catalog()
    {
        int count = await Connection!.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dmscs'
              AND TABLE_NAME = 'OwnershipToken'
              AND COLUMN_NAME IN ('Id', 'Description', 'CreatedAt', 'CreatedBy', 'LastModifiedAt', 'ModifiedBy', 'TenantId');
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
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dmscs'
              AND TABLE_NAME = 'ApiClient'
              AND COLUMN_NAME = 'CreatorOwnershipTokenId';
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
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = 'dmscs'
              AND TABLE_NAME = 'ApiClientOwnershipToken'
              AND CONSTRAINT_NAME = 'PK_ApiClientOwnershipToken'
              AND COLUMN_NAME IN ('ApiClientId', 'OwnershipTokenId');
            """
        );

        keyColumnCount.Should().Be(2);
    }

    [Test]
    public async Task It_prevents_tenant_deletion_when_ownership_tokens_reference_the_tenant()
    {
        string? deleteRule = await Connection!.ExecuteScalarAsync<string>(
            """
            SELECT delete_referential_action_desc
            FROM sys.foreign_keys
            WHERE name = 'FK_OwnershipToken_Tenant';
            """
        );

        deleteRule.Should().Be("NO_ACTION");
    }
}
