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
/// A frozen snapshot cannot move a row later within a still-open window, so the hazard that keeps a
/// live min-only walk on DocumentId does not exist there and every windowed shape takes the
/// ContentVersion anchor. An unfiltered read still anchors on DocumentId: with no window predicate
/// there is nothing to seek, and routing a request to a snapshot must not by itself change the order
/// a collection is walked in.
/// </summary>
[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_Resolving_A_Snapshot_Query
{
    private readonly ChangeQueryPageOrderingPolicy _policy = new(useLegacyDocumentIdOrdering: false);

    [Test]
    public void It_orders_by_document_id_when_no_range_is_supplied()
    {
        _policy.ResolveForSnapshotQuery(null).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_when_the_range_has_no_bounds()
    {
        _policy.ResolveForSnapshotQuery(ChangeVersionRange.None).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_content_version_for_a_min_only_range()
    {
        // The one shape the two entry points disagree on, and the whole point of this story.
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(100L, null))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_a_zero_minimum()
    {
        // Zero is a bound like any other: resolution turns on the bound being present, not truthy.
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(0L, null))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_a_bounded_range()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(100L, 200L))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_a_max_only_range()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(null, 200L))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_a_zero_maximum()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(null, 0L))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_by_content_version_for_an_inverted_range()
    {
        // Rejected upstream, but the resolved anchor must still be deterministic: both bounds parsed.
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(200L, 100L))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }
}

[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_Resolving_A_Snapshot_Query_With_The_Kill_Switch_Enabled
{
    private readonly ChangeQueryPageOrderingPolicy _policy = new(useLegacyDocumentIdOrdering: true);

    [Test]
    public void It_orders_by_document_id_when_no_range_is_supplied()
    {
        _policy.ResolveForSnapshotQuery(null).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_when_the_range_has_no_bounds()
    {
        _policy.ResolveForSnapshotQuery(ChangeVersionRange.None).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_min_only_range()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(100L, null))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_bounded_range()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(100L, 200L))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_max_only_range()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(null, 200L))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_by_document_id_for_a_zero_maximum()
    {
        _policy
            .ResolveForSnapshotQuery(new ChangeVersionRange(null, 0L))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }
}

/// <summary>
/// The one rule stated whole: the two entry points resolve the same anchor for every window shape
/// except a min-only one, where the live rule keeps DocumentId and the snapshot rule takes
/// ContentVersion. A change that made either entry point depend on anything else — paging shape,
/// parameter presence, the magnitude of a bound — fails here rather than in one shape's own test.
/// </summary>
[TestFixture]
public class Given_Both_ChangeQueryPageOrderingPolicy_Entry_Points
{
    private readonly ChangeQueryPageOrderingPolicy _policy = new(useLegacyDocumentIdOrdering: false);

    private static IEnumerable<TestCaseData> EveryWindowShape()
    {
        yield return new TestCaseData((ChangeVersionRange?)null).SetName("No range at all");
        yield return new TestCaseData(ChangeVersionRange.None).SetName("A range with no bounds");
        yield return new TestCaseData(new ChangeVersionRange(100L, null)).SetName("Min-only");
        yield return new TestCaseData(new ChangeVersionRange(0L, null)).SetName("Min-only at zero");
        yield return new TestCaseData(new ChangeVersionRange(null, 200L)).SetName("Max-only");
        yield return new TestCaseData(new ChangeVersionRange(null, 0L)).SetName("Max-only at zero");
        yield return new TestCaseData(new ChangeVersionRange(100L, 200L)).SetName("Bounded");
        yield return new TestCaseData(new ChangeVersionRange(200L, 100L)).SetName("Inverted");
    }

    [TestCaseSource(nameof(EveryWindowShape))]
    public void It_diverges_from_the_live_rule_only_on_a_min_only_window(ChangeVersionRange? range)
    {
        PageOrderingMode live = _policy.ResolveForLiveQuery(range);
        PageOrderingMode snapshot = _policy.ResolveForSnapshotQuery(range);

        if (range is { MinChangeVersion: not null, MaxChangeVersion: null })
        {
            live.Should().Be(PageOrderingMode.DocumentId, "a live min-only walk can see a row move");
            snapshot.Should().Be(PageOrderingMode.ContentVersion, "nothing moves inside a frozen snapshot");
            return;
        }

        snapshot.Should().Be(live, "only a min-only window is resolved differently by data source");
    }
}

/// <summary>
/// Both entry points read a parsed <see cref="ChangeVersionRange"/>, so the presence of a
/// change-version query parameter is not the same thing as a bound. These drive the real parse to
/// prove which raw values reach the policy as a null bound — and which, despite looking irregular,
/// reach it as a real one.
/// </summary>
[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_Resolving_From_Parsed_Query_Parameters
{
    private readonly ChangeQueryPageOrderingPolicy _conditionalPolicy = new(
        useLegacyDocumentIdOrdering: false
    );
    private readonly ChangeQueryPageOrderingPolicy _legacyPolicy = new(useLegacyDocumentIdOrdering: true);

    private static ChangeVersionRange Parse(params (string Name, string Value)[] queryParameters) =>
        ChangeVersionParameterValidator
            .Validate(
                queryParameters.ToDictionary(
                    static parameter => parameter.Name,
                    static parameter => parameter.Value
                )
            )
            .Range;

    private static PageOrderingMode Resolve(
        ChangeQueryPageOrderingPolicy policy,
        params (string Name, string Value)[] queryParameters
    )
    {
        return policy.ResolveForLiveQuery(Parse(queryParameters));
    }

    private static PageOrderingMode ResolveSnapshot(
        ChangeQueryPageOrderingPolicy policy,
        params (string Name, string Value)[] queryParameters
    )
    {
        return policy.ResolveForSnapshotQuery(Parse(queryParameters));
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

    [Test]
    [TestCase("", TestName = "Snapshot, empty minChangeVersion")]
    [TestCase("   ", TestName = "Snapshot, whitespace minChangeVersion")]
    [TestCase("abc", TestName = "Snapshot, non-numeric minChangeVersion")]
    [TestCase("-1", TestName = "Snapshot, negative minChangeVersion")]
    public void It_orders_a_snapshot_by_document_id_when_the_minimum_does_not_parse(string rawValue)
    {
        // A bound the parser rejected is an absent bound. The request is about to be refused for the
        // parameter anyway, and an anchor resolved from a bound that never existed would be one the
        // client could not reproduce.
        ResolveSnapshot(_conditionalPolicy, ("minChangeVersion", rawValue))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    [TestCase("", TestName = "Snapshot, empty maxChangeVersion")]
    [TestCase("abc", TestName = "Snapshot, non-numeric maxChangeVersion")]
    public void It_orders_a_snapshot_by_document_id_when_the_maximum_does_not_parse(string rawValue)
    {
        ResolveSnapshot(_conditionalPolicy, ("maxChangeVersion", rawValue))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }

    [Test]
    public void It_orders_a_snapshot_by_content_version_for_a_parsed_minimum()
    {
        // The divergence, reached through the real parse rather than a hand-built range.
        ResolveSnapshot(_conditionalPolicy, ("minChangeVersion", "100"))
            .Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    [TestCase("MINCHANGEVERSION", TestName = "Snapshot, upper-case minimum key")]
    [TestCase("MinChangeVersion", TestName = "Snapshot, pascal-case minimum key")]
    public void It_orders_a_snapshot_by_content_version_for_a_case_variant_minimum_key(string key)
    {
        // A case-variant key is not an irregular value: the parser matches keys case-insensitively,
        // so this is a present bound and anchors like any other. Grouping it with the unparseable
        // values above would assert the opposite of what the parser does.
        ResolveSnapshot(_conditionalPolicy, (key, "100")).Should().Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_orders_an_unfiltered_snapshot_read_by_document_id()
    {
        ResolveSnapshot(_conditionalPolicy).Should().Be(PageOrderingMode.DocumentId);
    }

    [Test]
    [TestCase("100", "", TestName = "Legacy switch over a snapshot min-only window")]
    [TestCase("100", "200", TestName = "Legacy switch over a snapshot bounded window")]
    [TestCase("", "200", TestName = "Legacy switch over a snapshot max-only window")]
    public void It_orders_a_snapshot_by_document_id_for_every_window_when_the_kill_switch_is_enabled(
        string minimum,
        string maximum
    )
    {
        ResolveSnapshot(_legacyPolicy, ("minChangeVersion", minimum), ("maxChangeVersion", maximum))
            .Should()
            .Be(PageOrderingMode.DocumentId);
    }
}
