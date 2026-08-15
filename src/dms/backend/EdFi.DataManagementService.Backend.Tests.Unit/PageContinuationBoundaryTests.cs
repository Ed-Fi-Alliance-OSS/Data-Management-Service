// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The selected maximum and whether it may anchor a continuation are two independent facts, resolved
/// together so the regular-resource and descriptor paths cannot answer the same page differently.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Page_Continuation_Boundary
{
    private static readonly CollectionPaging _traditionalPaging = new CollectionPaging.Traditional(
        new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
    );

    private static readonly CollectionPaging _cursorPaging = new CollectionPaging.Cursor(
        CursorRange.From(1),
        new PageSize(25)
    );

    [Test]
    public void It_allows_continuation_for_a_document_id_ordered_traditional_page()
    {
        PageContinuationBoundary
            .For(_traditionalPaging, PageOrderingMode.DocumentId, 2509L)
            .Should()
            .Be(new PageContinuationBoundary(2509L, AllowsDocumentIdContinuation: true));
    }

    [Test]
    public void It_disallows_continuation_for_a_content_version_ordered_traditional_page()
    {
        PageContinuationBoundary
            .For(_traditionalPaging, PageOrderingMode.ContentVersion, 2509L)
            .Should()
            .Be(new PageContinuationBoundary(2509L, AllowsDocumentIdContinuation: false));
    }

    // Cursor selection carries no ordering choice at all: it is always ordered by DocumentId, so its
    // token anchor is always the key its own page was ordered by.
    [TestCase(PageOrderingMode.DocumentId)]
    [TestCase(PageOrderingMode.ContentVersion)]
    public void It_allows_continuation_for_a_cursor_page_whatever_the_resolved_ordering(
        PageOrderingMode orderingMode
    )
    {
        PageContinuationBoundary
            .For(_cursorPaging, orderingMode, 2509L)
            .Should()
            .Be(new PageContinuationBoundary(2509L, AllowsDocumentIdContinuation: true));
    }

    [Test]
    public void It_keeps_a_null_selected_maximum_null()
    {
        PageContinuationBoundary
            .For(_traditionalPaging, PageOrderingMode.ContentVersion, null)
            .SelectedMaximum.Should()
            .BeNull();
        PageContinuationBoundary
            .For(_cursorPaging, PageOrderingMode.DocumentId, null)
            .SelectedMaximum.Should()
            .BeNull();
    }

    // Eligibility is a property of the page's ordering, not of whether the page selected anything, so a
    // page that selected nothing still reports the eligibility its ordering implies.
    [Test]
    public void It_decides_eligibility_independently_of_the_selected_maximum()
    {
        PageContinuationBoundary
            .For(_traditionalPaging, PageOrderingMode.ContentVersion, null)
            .AllowsDocumentIdContinuation.Should()
            .BeFalse();
        PageContinuationBoundary
            .For(_traditionalPaging, PageOrderingMode.DocumentId, null)
            .AllowsDocumentIdContinuation.Should()
            .BeTrue();
    }

    [TestCase(long.MinValue)]
    [TestCase(0L)]
    [TestCase(long.MaxValue)]
    public void It_returns_the_selected_maximum_unmodified(long selectedMaximum)
    {
        PageContinuationBoundary
            .For(_traditionalPaging, PageOrderingMode.DocumentId, selectedMaximum)
            .SelectedMaximum.Should()
            .Be(selectedMaximum);
    }
}
