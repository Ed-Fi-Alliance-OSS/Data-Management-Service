// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlDescriptorProjectionSeedSupport
{
    public static async Task<int> SeedAsync(SqlConnection connection, long documentId, string ns, string code)
    {
        await using SqlCommand command = new(
            """
            INSERT INTO [dms].[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId])
            VALUES (@documentId, @uuid, 0);
            DECLARE @descriptor TABLE ([DescriptorId] int);
            INSERT INTO [dms].[Descriptor] ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription])
            OUTPUT inserted.[DescriptorId] INTO @descriptor
            VALUES (@documentId, 0, @namespace, @code, @code);
            SELECT [DescriptorId] FROM @descriptor;
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
