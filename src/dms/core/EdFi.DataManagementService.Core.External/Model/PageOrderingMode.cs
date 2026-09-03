// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The page-selection ordering key, and therefore the page anchor: the column a page's cursor
/// bounds, partition boundaries, and continuation token are all expressed in.
/// </summary>
/// <remarks>
/// Lives in <c>Core.External</c> because both sides of the request need it and there is only one
/// rule: Core resolves the mode from the change-version window and the data store serving it, stamps
/// it on the request and on the token, and the backend compiles the candidate SQL against the column
/// it names. A Core-side twin plus a mapping function would be two places for that one rule to drift.
/// </remarks>
public enum PageOrderingMode
{
    /// <summary>Order page selection by the root table's <c>DocumentId</c>. The default.</summary>
    DocumentId,

    /// <summary>
    /// Order page selection by the root table's mirrored <c>ContentVersion</c> column. Selected for a
    /// max-bearing change-version window against any data store, and for every windowed shape —
    /// min-only included — against a frozen snapshot; see <c>ChangeQueryPageOrderingPolicy</c>.
    /// </summary>
    ContentVersion,
}
