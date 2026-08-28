// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_With_The_Kill_Switch_Disabled
{
    private readonly ChangeQueryPageOrderingPolicy _policy = new(useLegacyDocumentIdOrdering: false);

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

    [Test]
    public void It_orders_by_content_version_for_a_zero_maximum()
    {
        _policy
            .ResolveForLiveQuery(new ChangeVersionRange(null, 0L))
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

/// <summary>
/// The policy reads a parsed <see cref="ChangeVersionRange"/>, so presence of the maxChangeVersion
/// query parameter is not the same thing as a max-bearing window. These drive the real parse to
/// prove which raw values reach the policy as a null maximum, and therefore anchor on DocumentId.
/// </summary>
[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_Resolving_From_Parsed_Query_Parameters
{
    private readonly ChangeQueryPageOrderingPolicy _conditionalPolicy = new(
        useLegacyDocumentIdOrdering: false
    );
    private readonly ChangeQueryPageOrderingPolicy _legacyPolicy = new(useLegacyDocumentIdOrdering: true);

    private static PageOrderingMode Resolve(
        ChangeQueryPageOrderingPolicy policy,
        params (string Name, string Value)[] queryParameters
    )
    {
        var parsed = ChangeVersionParameterValidator.Validate(
            queryParameters.ToDictionary(
                static parameter => parameter.Name,
                static parameter => parameter.Value
            )
        );

        return policy.ResolveForLiveQuery(parsed.Range);
    }

    [Test]
    public void It_orders_by_content_version_for_a_parsed_maximum()
    {
        Resolve(_conditionalPolicy, ("maxChangeVersion", "200")).Should().Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    [TestCase("", TestName = "Empty maxChangeVersion")]
    [TestCase("   ", TestName = "Whitespace maxChangeVersion")]
    public void It_orders_by_document_id_when_the_maximum_is_present_but_parses_to_null(string rawValue)
    {
        Resolve(_conditionalPolicy, ("maxChangeVersion", rawValue)).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    [TestCase("abc", TestName = "Non-numeric maxChangeVersion")]
    [TestCase("-1", TestName = "Negative maxChangeVersion")]
    public void It_orders_by_document_id_when_the_maximum_is_unparseable(string rawValue)
    {
        Resolve(_conditionalPolicy, ("maxChangeVersion", rawValue)).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_min_only_window()
    {
        Resolve(_conditionalPolicy, ("minChangeVersion", "100")).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_content_version_for_a_bounded_window()
    {
        Resolve(_conditionalPolicy, ("minChangeVersion", "100"), ("maxChangeVersion", "200"))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_an_inverted_window()
    {
        // An inverted window is rejected upstream, but the resolved anchor must still be
        // deterministic: the maximum parsed, so the window is max-bearing.
        Resolve(_conditionalPolicy, ("minChangeVersion", "200"), ("maxChangeVersion", "100"))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_a_case_variant_parameter_name()
    {
        Resolve(_conditionalPolicy, ("MAXCHANGEVERSION", "200")).Should().Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_document_id_for_an_unfiltered_query()
    {
        Resolve(_conditionalPolicy).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    [TestCase("200", TestName = "Legacy switch over a parsed maximum")]
    [TestCase("", TestName = "Legacy switch over an empty maximum")]
    [TestCase("abc", TestName = "Legacy switch over an unparseable maximum")]
    public void It_orders_by_document_id_for_every_window_when_the_kill_switch_is_enabled(string rawValue)
    {
        Resolve(_legacyPolicy, ("minChangeVersion", "100"), ("maxChangeVersion", rawValue))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }
}
