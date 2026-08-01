// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public sealed partial class MssqlGeneratedDdlTestDatabase
{
    /// <summary>
    /// Runs a raw root-table seeding statement with the table's triggers disabled, then re-asserts the
    /// <c>dms.Document</c> metadata mirror on the seeded root row before re-enabling them.
    /// </summary>
    /// <remarks>
    /// The root stamping trigger is what normally dual-writes <c>DocumentUuid</c>, the content stamp, and
    /// the identity stamp from <c>dms.Document</c> onto the root row; disabling it leaves those mirrors at
    /// their column defaults (<c>newid()</c> / <c>0</c> / <c>sysutcdatetime()</c>). Read paths now source
    /// document metadata — including the GET-by-id target probe on <c>UX_&lt;Root&gt;_DocumentUuid</c> —
    /// from those mirrors, so a seeder that bypasses the trigger must restore the invariant itself. The
    /// mirror runs while triggers are still disabled so it does not itself bump the content stamp.
    /// </remarks>
    public async Task ExecuteWithTriggersTemporarilyDisabledAsync(
        string schema,
        string table,
        Func<Task> action,
        long? mirrorMetadataForDocumentId = null
    )
    {
        await ExecuteNonQueryAsync($"""DISABLE TRIGGER ALL ON [{schema}].[{table}];""");

        try
        {
            await action();

            if (mirrorMetadataForDocumentId is { } documentId)
            {
                await MirrorDocumentMetadataOntoRootAsync(schema, table, documentId);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync($"""ENABLE TRIGGER ALL ON [{schema}].[{table}];""");
        }
    }

    private async Task MirrorDocumentMetadataOntoRootAsync(string schema, string table, long documentId)
    {
        await ExecuteNonQueryAsync(
            $"""
            UPDATE root
            SET root.[DocumentUuid] = document.[DocumentUuid],
                root.[ContentVersion] = document.[ContentVersion],
                root.[ContentLastModifiedAt] = document.[ContentLastModifiedAt],
                root.[IdentityVersion] = document.[IdentityVersion],
                root.[IdentityLastModifiedAt] = document.[IdentityLastModifiedAt],
                root.[CreatedAt] = document.[CreatedAt]
            FROM [{schema}].[{table}] root
            INNER JOIN [dms].[Document] document ON document.[DocumentId] = root.[DocumentId]
            WHERE root.[DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }
}
