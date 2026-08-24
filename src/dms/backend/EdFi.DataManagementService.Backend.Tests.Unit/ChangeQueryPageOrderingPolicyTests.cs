// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_With_The_Kill_Switch_Disabled
{
    private readonly ChangeQueryPageOrderingPolicy _policy = ChangeQueryPageOrderingPolicy.Default;

    [Test]
    public void It_orders_by_document_id_when_no_range_is_supplied()
    {
        _policy.ResolveForLiveQuery(null).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_when_the_range_has_no_bounds()
    {
        _policy.ResolveForLiveQuery(ChangeVersionRange.None).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_min_only_range()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(100L, null))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_content_version_for_a_bounded_range()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(100L, 200L))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_a_max_only_range()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(null, 200L))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }
}

[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_With_The_Kill_Switch_Enabled
{
    private readonly ChangeQueryPageOrderingPolicy _policy = new(useLegacyDocumentIdOrdering: true);

    [Test]
    public void It_orders_by_document_id_when_no_range_is_supplied()
    {
        _policy.ResolveForLiveQuery(null).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_when_the_range_has_no_bounds()
    {
        _policy.ResolveForLiveQuery(ChangeVersionRange.None).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_min_only_range()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(100L, null))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_bounded_range()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(100L, 200L))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_max_only_range()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(null, 200L))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }
}
