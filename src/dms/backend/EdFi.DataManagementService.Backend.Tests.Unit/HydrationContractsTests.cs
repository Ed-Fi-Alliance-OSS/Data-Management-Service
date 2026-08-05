// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The hydrated page carries the maximum DocumentId of the selected page keyset alongside, and
/// independent of, the hydrated body. Hydration execution populates it in a later story.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Hydrated_Page
{
    private HydratedPage _page = null!;

    [SetUp]
    public void Setup()
    {
        _page = new HydratedPage(TotalCount: null, DocumentMetadata: [], [], []);
    }

    [Test]
    public void It_has_no_selected_keyset_boundary_by_default()
    {
        _page.HighestSelectedDocumentId.Should().BeNull();
    }

    [Test]
    public void It_accepts_a_selected_keyset_boundary()
    {
        HydratedPage withBoundary = _page with { HighestSelectedDocumentId = 2509 };

        withBoundary.HighestSelectedDocumentId.Should().Be(2509);
    }

    [Test]
    public void It_keeps_the_selected_keyset_boundary_independent_of_the_hydrated_body()
    {
        HydratedPage withBoundary = _page with { HighestSelectedDocumentId = 2509 };

        withBoundary.DocumentMetadata.Should().BeEmpty();
        withBoundary.HighestSelectedDocumentId.Should().Be(2509);
    }

    [Test]
    public void It_carries_the_boundary_as_a_nullable_long()
    {
        typeof(HydratedPage)
            .GetProperty(nameof(HydratedPage.HighestSelectedDocumentId))!
            .PropertyType.Should()
            .Be<long?>();
    }
}
