// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>Native compact identities for the standalone projection fixtures sharing the assertion database.</summary>
internal static class PostgresqlDescriptorProjectionSeedSupport
{
    public static async Task<int> SeedAsync(
        NpgsqlConnection connection,
        long documentId,
        string ns,
        string code
    )
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
            VALUES (@documentId, @uuid, 0);
            INSERT INTO dms."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription")
            VALUES (@documentId, 0, @namespace, @code, @code)
            RETURNING "DescriptorId";
            """,
            connection
        );
        command.Parameters.AddWithValue("documentId", documentId);
        command.Parameters.AddWithValue("uuid", Guid.NewGuid());
        command.Parameters.AddWithValue("namespace", ns);
        command.Parameters.AddWithValue("code", code);
        int descriptorId = (int)(await command.ExecuteScalarAsync())!;
        documentId.Should().BeGreaterThan(int.MaxValue);
        ((long)descriptorId).Should().NotBe(documentId);
        return descriptorId;
    }
}
