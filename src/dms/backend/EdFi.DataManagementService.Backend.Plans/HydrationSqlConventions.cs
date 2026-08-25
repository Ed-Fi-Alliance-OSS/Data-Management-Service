// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Shared conventions for generated hydration SQL.
/// </summary>
internal static class HydrationSqlConventions
{
    public const string SingleDocumentIdParameterName = "DocumentId";

    public const string SelectedPageDocumentIdsJsonParameterName = "selectedDocumentIdsJson";

    public const string SelectedPageOrdinalColumnName = "Ordinal";

    /// <summary>
    /// The keyset table column carrying the continuation anchor of a <c>ContentVersion</c>-anchored
    /// page. Nullable and present only on those pages, so a <c>DocumentId</c>-anchored batch emits the
    /// keyset SQL it always has.
    /// </summary>
    public const string SelectedAnchorColumnName = "ContentVersion";

    public static string SerializeSelectedPageDocumentIds(IReadOnlyList<long> documentIds)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        return JsonSerializer.Serialize(
            documentIds.Select(
                static (documentId, ordinal) => new SelectedPageDocumentId(documentId, ordinal)
            )
        );
    }

    private sealed record SelectedPageDocumentId(long DocumentId, int Ordinal);
}
