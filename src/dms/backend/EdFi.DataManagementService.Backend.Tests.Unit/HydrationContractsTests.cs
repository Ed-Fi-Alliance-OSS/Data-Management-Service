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
        _page.HighestSelectedAnchor.Should().BeNull();
    }

    [Test]
    public void It_accepts_a_selected_keyset_boundary()
    {
        HydratedPage withBoundary = _page with { HighestSelectedAnchor = 2509 };

        withBoundary.HighestSelectedAnchor.Should().Be(2509);
    }

    [Test]
    public void It_keeps_the_selected_keyset_boundary_independent_of_the_hydrated_body()
    {
        HydratedPage withBoundary = _page with { HighestSelectedAnchor = 2509 };

        withBoundary.DocumentMetadata.Should().BeEmpty();
        withBoundary.HighestSelectedAnchor.Should().Be(2509);
    }

    [Test]
    public void It_carries_the_boundary_as_a_nullable_long()
    {
        typeof(HydratedPage)
            .GetProperty(nameof(HydratedPage.HighestSelectedAnchor))!
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
    private static PageKeysetSpec.Query CreateKeyset(PageOrderingMode orderingMode)
    {
        var plan = new PageDocumentIdSqlPlan(
            "SELECT 1;",
            TotalCountSql: null,
            PageParametersInOrder: [],
            TotalCountParametersInOrder: null
        );

        return new PageKeysetSpec.Query(plan, new Dictionary<string, object?>(), orderingMode);
    }

    [TestCase(PageOrderingMode.DocumentId)]
    [TestCase(PageOrderingMode.ContentVersion)]
    public void It_carries_the_anchor_it_was_built_with(PageOrderingMode orderingMode)
    {
        CreateKeyset(orderingMode).OrderingMode.Should().Be(orderingMode);
    }

    /// <summary>
    /// The anchor is a required argument, not a defaulted one. It is the value the batch builder and
    /// both keyset readers branch on, and a keyset left on a default while its plan was compiled for
    /// <c>ContentVersion</c> still emits valid SQL — it just hands back <c>DocumentId</c>s that Core
    /// then stamps with a <c>ContentVersion</c> marker, which is a walk that skips rows and fails
    /// nowhere.
    /// </summary>
    [Test]
    public void It_requires_an_anchor_rather_than_defaulting_one()
    {
        typeof(PageKeysetSpec.Query)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == nameof(PageKeysetSpec.Query.OrderingMode))
            .HasDefaultValue.Should()
            .BeFalse();
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
