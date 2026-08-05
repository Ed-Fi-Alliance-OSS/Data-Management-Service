// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// How a live collection query pages: traditional limit/offset, or cursor over an inclusive
/// DocumentId range.
/// </summary>
/// <remarks>
/// An explicit choice rather than nullable combinations, so a cursor request without a range and a
/// traditional request with a page size are both unrepresentable. Change Query endpoints keep using
/// <see cref="PaginationParameters"/> directly and do not page by cursor.
/// </remarks>
public abstract record CollectionPaging
{
    private CollectionPaging() { }

    /// <summary>
    /// Whether this query asked for a total count. Only traditional paging can: cursor paging never
    /// requests or compiles a total count.
    /// </summary>
    public abstract bool IncludesTotalCount { get; }

    /// <summary>
    /// Traditional limit / offset / totalCount paging.
    /// </summary>
    /// <param name="Parameters">The client-supplied traditional paging inputs.</param>
    public sealed record Traditional(PaginationParameters Parameters) : CollectionPaging
    {
        public override bool IncludesTotalCount => Parameters.TotalCount;
    }

    /// <summary>
    /// Cursor paging over an inclusive DocumentId range.
    /// </summary>
    /// <param name="Range">The inclusive DocumentId window to select from.</param>
    /// <param name="PageSize">The number of items the page may select.</param>
    public sealed record Cursor(CursorRange Range, PageSize PageSize) : CollectionPaging
    {
        public override bool IncludesTotalCount => false;
    }
}
