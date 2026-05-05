// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal sealed class ProfileGuardedNoOpAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

internal sealed class ProfileGuardedNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

internal sealed record ProfileGuardedNoOpDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTime ContentLastModifiedAt,
    long IdentityVersion,
    DateTime IdentityLastModifiedAt
);

internal sealed record ProfileGuardedNoOpPersistedState(
    ProfileGuardedNoOpDocumentRow Document,
    IReadOnlyDictionary<string, object?> RootRow,
    long DocumentChangeEventCount
);

internal static class ProfileGuardedNoOpPersistedStateSupport
{
    public static async Task<ProfileGuardedNoOpPersistedState> ReadPersistedStateAsync<TDatabase>(
        TDatabase database,
        Guid documentUuid,
        Func<TDatabase, Guid, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> readDocumentRows,
        Func<TDatabase, long, Task<IReadOnlyDictionary<string, object?>>> readRootRowByDocumentId,
        Func<TDatabase, long, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> readChangeEventRows
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(readDocumentRows);
        ArgumentNullException.ThrowIfNull(readRootRowByDocumentId);
        ArgumentNullException.ThrowIfNull(readChangeEventRows);

        var documentRows = await readDocumentRows(database, documentUuid).ConfigureAwait(false);
        var documentRow = BuildDocumentRow(documentRows, documentUuid);

        var rootRow = await readRootRowByDocumentId(database, documentRow.DocumentId).ConfigureAwait(false);
        var changeEventRows = await readChangeEventRows(database, documentRow.DocumentId)
            .ConfigureAwait(false);
        var changeEventCount = ExtractSingleRowCount(changeEventRows);

        return new ProfileGuardedNoOpPersistedState(documentRow, rootRow, changeEventCount);
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
            )
        );
    }

    private static long ExtractSingleRowCount(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one DocumentChangeEvent count row, but found {rows.Count}."
            );
        }

        return Convert.ToInt64(rows[0]["RowCount"], CultureInfo.InvariantCulture);
    }
}
