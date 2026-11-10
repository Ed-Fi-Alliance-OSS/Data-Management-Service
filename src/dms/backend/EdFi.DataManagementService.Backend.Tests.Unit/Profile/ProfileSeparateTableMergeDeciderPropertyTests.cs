// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CsCheck;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileCollectionMergeTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
[Category("ProfileProperty")]
public class Given_Generated_Profile_Separate_Table_Merges
{
    public enum Presence
    {
        Missing,
        Present,
        Absent,
        Hidden,
    }

    // Independent decision table: unlisted combinations are caller-contract failures.
    // Insert is further restricted by the generated request's creatability.
    private static readonly IReadOnlyDictionary<
        (Presence Request, Presence Stored, bool Row),
        ProfileSeparateTableMergeOutcome
    > _outcomes = new Dictionary<(Presence, Presence, bool), ProfileSeparateTableMergeOutcome>
    {
        [(Presence.Present, Presence.Missing, false)] = ProfileSeparateTableMergeOutcome.Insert,
        [(Presence.Present, Presence.Present, false)] = ProfileSeparateTableMergeOutcome.Insert,
        [(Presence.Present, Presence.Absent, false)] = ProfileSeparateTableMergeOutcome.Insert,
        [(Presence.Present, Presence.Hidden, false)] = ProfileSeparateTableMergeOutcome.Insert,
        [(Presence.Present, Presence.Missing, true)] = ProfileSeparateTableMergeOutcome.Insert,
        [(Presence.Present, Presence.Absent, true)] = ProfileSeparateTableMergeOutcome.Insert,
        [(Presence.Present, Presence.Present, true)] = ProfileSeparateTableMergeOutcome.Update,
        [(Presence.Absent, Presence.Present, true)] = ProfileSeparateTableMergeOutcome.Delete,
        [(Presence.Missing, Presence.Hidden, true)] = ProfileSeparateTableMergeOutcome.Preserve,
        [(Presence.Absent, Presence.Hidden, true)] = ProfileSeparateTableMergeOutcome.Preserve,
        [(Presence.Hidden, Presence.Hidden, true)] = ProfileSeparateTableMergeOutcome.Preserve,
    };

    private static IEnumerable<TestCaseData> Cases() =>
        from request in Enum.GetValues<Presence>()
        from stored in Enum.GetValues<Presence>()
        from row in new[] { false, true }
        select new TestCaseData(request, stored, row);

    private static ProfileVisibilityKind Visibility(Presence presence) =>
        presence switch
        {
            Presence.Present => ProfileVisibilityKind.VisiblePresent,
            Presence.Absent => ProfileVisibilityKind.VisibleAbsent,
            Presence.Hidden => ProfileVisibilityKind.Hidden,
            _ => throw new ArgumentOutOfRangeException(nameof(presence)),
        };

    [TestCaseSource(nameof(Cases))]
    public void It_obeys_the_decision_table_or_fails_closed(Presence request, Presence stored, bool row)
    {
        Gen.Select(Gen.Int[1, 1000], Gen.Bool)
            .Sample(
                (parentId, creatable) =>
                {
                    var scope = "$.parents[*].details";
                    ScopeInstanceAddress Address() =>
                        new(scope, [new("$.parents[*]", BuildSemanticIdentityLong("parentId", parentId))]);
                    ProfileSeparateTableMergeOutcome Act() =>
                        new ProfileSeparateTableMergeDecider().Decide(
                            scope,
                            request == Presence.Missing
                                ? null
                                : new RequestScopeState(Address(), Visibility(request), creatable),
                            stored == Presence.Missing
                                ? null
                                : new StoredScopeState(Address(), Visibility(stored), ["secret"]),
                            row
                        );
                    if (_outcomes.TryGetValue((request, stored, row), out var expected))
                    {
                        if (expected == ProfileSeparateTableMergeOutcome.Insert && !creatable)
                        {
                            expected = ProfileSeparateTableMergeOutcome.RejectCreateDenied;
                        }
                        Act().Should().Be(expected);
                    }
                    else
                    {
                        Action act = () => Act();
                        act.Should()
                            .ThrowExactly<InvalidOperationException>()
                            .WithMessage($"*{scope}*no actionable state*");
                    }
                },
                iter: 50,
                threads: 1
            );
    }
}
