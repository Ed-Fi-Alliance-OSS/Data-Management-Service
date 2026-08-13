// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// What a completed page selection can tell Core about continuing after it: the maximum DocumentId it
/// selected, and whether that maximum may anchor a continuation.
/// </summary>
/// <param name="SelectedMaximum">
/// The maximum DocumentId in the selected page keyset, or <see langword="null"/> when page selection
/// was skipped or selected no keys.
/// </param>
/// <param name="AllowsDocumentIdContinuation">
/// Whether <paramref name="SelectedMaximum"/> describes where this page ended, which it does only when
/// the page was ordered by DocumentId.
/// </param>
internal readonly record struct PageContinuationBoundary(
    long? SelectedMaximum,
    bool AllowsDocumentIdContinuation
)
{
    /// <summary>
    /// Pairs a page's selected maximum with its continuation eligibility. Regular-resource and
    /// descriptor query execution both resolve it here, so the two resource families cannot answer the
    /// same page differently.
    /// </summary>
    /// <remarks>
    /// A continuation anchored on DocumentId is only valid when DocumentId is the key the page was
    /// ordered by. Cursor selection always orders by DocumentId — its mode carries no ordering choice
    /// at all — so a cursor page is always eligible. Traditional selection orders by ContentVersion for
    /// a max-bearing change-version window, and a DocumentId anchor taken from such a page would skip
    /// qualifying rows with a smaller DocumentId and a later ContentVersion, so it is not eligible.
    /// The caller supplies the effective ordering rather than the request's filters, which is what
    /// keeps the legacy DocumentId-ordering switch honored: under it a windowed traditional page really
    /// is ordered by DocumentId, and really can anchor a continuation.
    /// </remarks>
    /// <param name="paging">The paging choice the page was selected with.</param>
    /// <param name="orderingMode">The effective page-selection ordering key.</param>
    /// <param name="selectedMaximum">The maximum selected DocumentId, unchanged by this decision.</param>
    public static PageContinuationBoundary For(
        CollectionPaging paging,
        PageOrderingMode orderingMode,
        long? selectedMaximum
    )
    {
        ArgumentNullException.ThrowIfNull(paging);

        var allowsDocumentIdContinuation = paging switch
        {
            CollectionPaging.Cursor => true,
            CollectionPaging.Traditional => orderingMode is PageOrderingMode.DocumentId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(paging),
                paging.GetType().Name,
                "Unsupported collection paging mode."
            ),
        };

        return new PageContinuationBoundary(selectedMaximum, allowsDocumentIdContinuation);
    }
}
