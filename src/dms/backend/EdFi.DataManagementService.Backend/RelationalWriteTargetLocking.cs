// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalWriteTargetLocking
{
    /// <summary>
    /// Locks the addressed document's row in <paramref name="lockTable"/> and returns its
    /// <c>ContentVersion</c>, or <see langword="null"/> when the row is gone.
    /// </summary>
    public static async Task<long?> TryLockExistingTargetAsync(
        SqlDialect dialect,
        DbTableName lockTable,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        await using var command = writeSession.CreateCommand(
            RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, lockTable, documentId)
        );

        var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalarResult is null or DBNull ? null : Convert.ToInt64(scalarResult);
    }
}
