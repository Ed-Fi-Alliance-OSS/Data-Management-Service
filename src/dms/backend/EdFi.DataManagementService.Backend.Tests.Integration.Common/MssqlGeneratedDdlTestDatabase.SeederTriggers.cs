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
    /// stamp the suppressed trigger would have written on the seeded root row before re-enabling them.
    /// </summary>
    /// <remarks>
    /// The root row owns its document metadata outright. The root stamping trigger is what normally
    /// writes the content and identity stamps; disabling it leaves them at their column defaults
    /// (<c>0</c> / <c>sysutcdatetime()</c>), which would hand the fixture a document whose stamps never
    /// came off <c>dms.ChangeVersionSequence</c> and therefore do not order against production-written
    /// rows. The re-assertion is root-local — there is no second table to copy from — and it runs while
    /// triggers are still disabled so it does not itself bump the stamp a second time.
    /// <c>DocumentUuid</c> is not touched: the seeding statement binds it directly.
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
            SET root.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
                root.[ContentLastModifiedAt] = SYSUTCDATETIME(),
                root.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
                root.[IdentityLastModifiedAt] = SYSUTCDATETIME()
            FROM [{schema}].[{table}] root
            WHERE root.[DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }
}
