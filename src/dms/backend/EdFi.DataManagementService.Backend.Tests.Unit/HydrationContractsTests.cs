// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
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

/// <summary>
/// A query keyset carries the anchor its plan was compiled against, which is what decides whether the
/// materialized keyset carries the continuation anchor out of selection alongside the ids.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Query_Page_Keyset_Spec
{
    private static PageKeysetSpec.Query CreateKeyset(PageOrderingMode? orderingMode = null)
    {
        var plan = new PageDocumentIdSqlPlan(
            "SELECT 1;",
            TotalCountSql: null,
            PageParametersInOrder: [],
            TotalCountParametersInOrder: null
        );
        var parameterValues = new Dictionary<string, object?>();

        return orderingMode is { } mode
            ? new PageKeysetSpec.Query(plan, parameterValues, mode)
            : new PageKeysetSpec.Query(plan, parameterValues);
    }

    [Test]
    public void It_anchors_on_document_id_by_default()
    {
        // The default is what keeps every keyset a caller builds without naming an anchor — GET-by-id
        // paths, traditional pages, and every existing fixture — emitting the batch text it always has.
        CreateKeyset().OrderingMode.Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_carries_a_content_version_anchor()
    {
        CreateKeyset(PageOrderingMode.ContentVersion)
            .OrderingMode.Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_preserves_the_anchor_when_a_copy_replaces_only_the_parameter_values()
    {
        // The anchor and the plan have to stay in step: the plan's projection is what the anchor names,
        // so a record rebuild that dropped one would compile SQL for a column it never projected.
        var anchored = CreateKeyset(PageOrderingMode.ContentVersion);

        var copy = anchored with { ParameterValues = new Dictionary<string, object?> { ["pageSize"] = 25L } };

        copy.OrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        copy.Plan.Should().BeSameAs(anchored.Plan);
    }

    [TestCase(typeof(PageKeysetSpec.Single))]
    [TestCase(typeof(PageKeysetSpec.SelectedPage))]
    public void It_is_the_only_keyset_variant_that_carries_an_anchor(Type variant)
    {
        // The other variants are handed their ids rather than selecting them, so they have no
        // continuation to anchor and must not grow a defaultable anchor a caller could set wrong.
        variant.GetProperty(nameof(PageKeysetSpec.Query.OrderingMode)).Should().BeNull();
    }
}
