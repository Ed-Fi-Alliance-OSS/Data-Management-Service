// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.External.Backend;
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

    /// <summary>
    /// <see cref="EveryWindowShape" /> is written out by hand, and it is exhaustive only because a
    /// window has exactly two optional bounds. This gate makes that a checked assumption rather than
    /// a remembered one: a third bound would add shapes the list does not name, and each would take
    /// whichever branch its minimum and maximum happened to imply, silently.
    /// <see cref="Given_ChangeQueryPageOrderingPolicy_Resolving_For_A_Data_Store_Kind" /> forces the
    /// same decision reflectively over the target kinds; this is the window half of it.
    /// <para>
    /// Paired with <see cref="It_lists_every_combination_of_the_bounds_it_accounts_for" />, which
    /// closes the other half: this one says two bounds is still the whole vocabulary, that one says
    /// the list actually spends it. Either alone leaves a hole - a third bound slips past the
    /// second, and a deleted <c>yield return</c> slips past the first.
    /// </para>
    /// </summary>
    [Test]
    public void It_accounts_for_every_window_bound()
    {
        typeof(ChangeVersionRange)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.PropertyType == typeof(long?))
            .Select(static property => property.Name)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    nameof(ChangeVersionRange.MinChangeVersion),
                    nameof(ChangeVersionRange.MaxChangeVersion),
                },
                "a new window bound must be added to EveryWindowShape() rather than defaulting into "
                    + "one of the shapes already listed there"
            );
    }

    /// <summary>
    /// Both entry points branch on bound <em>presence</em> alone, so the four presence combinations
    /// are the decision space every case in this fixture is drawn from. This gate says
    /// <see cref="EveryWindowShape" /> still spends all four: deleting a <c>yield return</c> would
    /// otherwise leave the shape it named untested while every remaining case passed, and
    /// <see cref="It_accounts_for_every_window_bound" /> would not notice because the type it
    /// inspects has not changed.
    /// </summary>
    /// <remarks>
    /// Presence, not value: the extra cases in the list - a zero bound, an inverted range - exist to
    /// pin that magnitude does not enter the decision, and they collapse onto the same combinations
    /// here rather than adding new ones.
    /// </remarks>
    [Test]
    public void It_lists_every_combination_of_the_bounds_it_accounts_for()
    {
        EveryWindowShape()
            .Select(static testCase => (ChangeVersionRange?)testCase.OriginalArguments[0])
            .Select(static range =>
                (
                    HasMinimum: range?.MinChangeVersion is not null,
                    HasMaximum: range?.MaxChangeVersion is not null
                )
            )
            .Distinct()
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (HasMinimum: false, HasMaximum: false),
                    (HasMinimum: true, HasMaximum: false),
                    (HasMinimum: false, HasMaximum: true),
                    (HasMinimum: true, HasMaximum: true),
                },
                "every combination of bound presence must stay represented in EveryWindowShape(), "
                    + "because that presence is the whole of what either entry point branches on"
            );
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

/// <summary>
/// The dispatch: which entry point a data-store kind resolves through. Being frozen for the life of
/// the walk is what qualifies a source for the snapshot rule, so only a snapshot takes it. A read
/// replica keeps applying changes, so a row can still move later within an open window there, and it
/// stays on the live rule along with the primary.
/// </summary>
/// <remarks>
/// Pinned on the policy rather than at the two middlewares that call it, because it is one rule and
/// they must not come to disagree about it: a boundary set cut under one and a page selected under the
/// other is a walk whose own tokens its follow-up requests reject. A new target kind that is not
/// frozen needs no change here and must not get one — the default is the live rule.
/// </remarks>
[TestFixture]
public class Given_ChangeQueryPageOrderingPolicy_Resolving_For_A_Data_Store_Kind
{
    private readonly ChangeQueryPageOrderingPolicy _policy = new(useLegacyDocumentIdOrdering: false);
    private readonly ChangeQueryPageOrderingPolicy _legacyPolicy = new(useLegacyDocumentIdOrdering: true);

    private static readonly ChangeVersionRange _minOnly = new(100L, null);

    /// <summary>
    /// Every window shape except min-only, which is the one shape the two entry points disagree on and
    /// so the one this fixture asserts separately.
    /// </summary>
    private static IEnumerable<TestCaseData> EveryShapeButMinOnly()
    {
        yield return new TestCaseData((ChangeVersionRange?)null).SetName("No range at all");
        yield return new TestCaseData(ChangeVersionRange.None).SetName("A range with no bounds");
        yield return new TestCaseData(new ChangeVersionRange(null, 200L)).SetName("Max-only");
        yield return new TestCaseData(new ChangeVersionRange(100L, 200L)).SetName("Bounded");
        yield return new TestCaseData(new ChangeVersionRange(200L, 100L)).SetName("Inverted");
    }

    [Test]
    public void It_resolves_a_snapshot_through_the_snapshot_entry_point()
    {
        _policy
            .ResolveFor(_minOnly, EffectiveTargetKind.Snapshot)
            .Should()
            .Be(_policy.ResolveForSnapshotQuery(_minOnly));
    }

    [TestCase(EffectiveTargetKind.Primary, TestName = "the primary is not frozen")]
    [TestCase(EffectiveTargetKind.ReadReplica, TestName = "a read replica is not frozen")]
    public void It_resolves_an_unfrozen_target_through_the_live_entry_point(EffectiveTargetKind targetKind)
    {
        _policy.ResolveFor(_minOnly, targetKind).Should().Be(_policy.ResolveForLiveQuery(_minOnly));
    }

    /// <summary>
    /// The same rule in the form a request actually experiences it, so a reader does not have to
    /// compose two entry points to see what a min-only walk gets from each data store.
    /// </summary>
    [TestCase(EffectiveTargetKind.Primary, PageOrderingMode.DocumentId, TestName = "min-only on the primary")]
    [TestCase(
        EffectiveTargetKind.ReadReplica,
        PageOrderingMode.DocumentId,
        TestName = "min-only on a read replica"
    )]
    [TestCase(
        EffectiveTargetKind.Snapshot,
        PageOrderingMode.ContentVersion,
        TestName = "min-only on a snapshot"
    )]
    public void It_anchors_a_min_only_window_by_data_store_kind(
        EffectiveTargetKind targetKind,
        PageOrderingMode expected
    )
    {
        _policy.ResolveFor(_minOnly, targetKind).Should().Be(expected);
    }

    /// <summary>
    /// Every other shape resolves alike on every kind, which is what makes those tokens replayable
    /// across a change of data source — the behavior CURSOR-PAGING.md documents for clients.
    /// </summary>
    [TestCaseSource(nameof(EveryShapeButMinOnly))]
    public void It_anchors_every_other_window_shape_alike_on_every_kind(ChangeVersionRange? range)
    {
        PageOrderingMode onThePrimary = _policy.ResolveFor(range, EffectiveTargetKind.Primary);

        _policy.ResolveFor(range, EffectiveTargetKind.ReadReplica).Should().Be(onThePrimary);
        _policy.ResolveFor(range, EffectiveTargetKind.Snapshot).Should().Be(onThePrimary);
    }

    [TestCase(EffectiveTargetKind.Primary, TestName = "legacy switch, primary")]
    [TestCase(EffectiveTargetKind.ReadReplica, TestName = "legacy switch, read replica")]
    [TestCase(EffectiveTargetKind.Snapshot, TestName = "legacy switch, snapshot")]
    public void It_anchors_on_the_document_id_for_every_kind_when_the_kill_switch_is_enabled(
        EffectiveTargetKind targetKind
    )
    {
        _legacyPolicy.ResolveFor(_minOnly, targetKind).Should().Be(PageOrderingMode.DocumentId);
    }

    /// <summary>
    /// The kinds asserted above are the whole enum.
    /// <see cref="ChangeQueryPageOrderingPolicy.ResolveFor" /> sends every kind that is not
    /// <see cref="EffectiveTargetKind.Snapshot" /> to the live rule, which is right only while every
    /// other kind is unfrozen. A kind added later would take the live rule silently and, if it were
    /// frozen, lose the seek this rule exists to give it — so the decision is forced here instead.
    /// </summary>
    [Test]
    public void It_accounts_for_every_data_store_kind()
    {
        EffectiveTargetKind[] classified =
        [
            EffectiveTargetKind.Primary,
            EffectiveTargetKind.ReadReplica,
            EffectiveTargetKind.Snapshot,
        ];

        Enum.GetValues<EffectiveTargetKind>()
            .Should()
            .BeEquivalentTo(
                classified,
                "a new target kind must be classified as frozen or unfrozen here rather than "
                    + "defaulting to the live rule unnoticed"
            );
    }
}
