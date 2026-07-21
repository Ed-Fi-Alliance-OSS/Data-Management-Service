// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal sealed record ProfileGuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTime ContentLastModifiedAt,
    long IdentityVersion,
    DateTime IdentityLastModifiedAt,
    DateTime CreatedAt
);

internal sealed record ProfileGuardedNoOpPersistedState(
    ProfileGuardedNoOpDocumentRow Document,
    IReadOnlyDictionary<string, object?> RootRow,
    long MaxChangeVersion
);

internal static class ProfileGuardedNoOpPersistedStateSupport
{
    public static async Task<ProfileGuardedNoOpPersistedState> ReadPersistedStateAsync<TDatabase>(
        TDatabase database,
        Guid documentUuid,
        Func<TDatabase, Guid, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> readDocumentRows,
        Func<TDatabase, long, Task<IReadOnlyDictionary<string, object?>>> readRootRowByDocumentId,
        Func<TDatabase, Task<long>> readMaxChangeVersion
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(readDocumentRows);
        ArgumentNullException.ThrowIfNull(readRootRowByDocumentId);
        ArgumentNullException.ThrowIfNull(readMaxChangeVersion);

        var documentRows = await readDocumentRows(database, documentUuid).ConfigureAwait(false);
        var documentRow = BuildDocumentRow(documentRows, documentUuid);

        var rootRow = await readRootRowByDocumentId(database, documentRow.DocumentId).ConfigureAwait(false);
        var maxChangeVersion = await readMaxChangeVersion(database).ConfigureAwait(false);

        return new ProfileGuardedNoOpPersistedState(documentRow, rootRow, maxChangeVersion);
    }

    private static ProfileGuardedNoOpDocumentRow BuildDocumentRow(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> documentRows,
        Guid documentUuid
    )
    {
        if (documentRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {documentRows.Count}."
            );
        }

        var row = documentRows[0];

        return new ProfileGuardedNoOpDocumentRow(
            DocumentId: Convert.ToInt64(row["DocumentId"], CultureInfo.InvariantCulture),
            DocumentUuid: (Guid)row["DocumentUuid"]!,
            ResourceKeyId: Convert.ToInt16(row["ResourceKeyId"], CultureInfo.InvariantCulture),
            ContentVersion: Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            ContentLastModifiedAt: Convert.ToDateTime(
                row["ContentLastModifiedAt"],
                CultureInfo.InvariantCulture
            ),
            IdentityVersion: Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            IdentityLastModifiedAt: Convert.ToDateTime(
                row["IdentityLastModifiedAt"],
                CultureInfo.InvariantCulture
            ),
            CreatedAt: Convert.ToDateTime(row["CreatedAt"], CultureInfo.InvariantCulture)
        );
    }
}
