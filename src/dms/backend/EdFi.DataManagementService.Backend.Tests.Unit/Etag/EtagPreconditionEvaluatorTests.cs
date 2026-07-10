// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Etag;

[TestFixture]
[Parallelizable]
public class Given_EtagPreconditionEvaluator
{
    private const string CurrentEtag = "5-a1b2c3d4.j._.l.i";
    private const string MatchingClientTag = "5-a1b2c3d4.x.3.n.g";
    private const string DifferingClientTag = "6-a1b2c3d4.j._.l.i";

    [TestCase(true)]
    [TestCase(false)]
    public void It_always_satisfies_when_no_precondition_regardless_of_target_existence(bool targetExists)
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.None(), targetExists, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_satisfies_IfMatch_when_projection_matches_and_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), true, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_satisfies_IfMatch_directly_from_current_state_when_projection_matches()
    {
        EtagPreconditionEvaluator
            .IsSatisfiedByCurrentState(
                new WritePrecondition.IfMatch(MatchingClientTag),
                contentVersion: 5,
                effectiveSchemaHash: "a1b2c3d4ffffffff"
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_satisfy_IfMatch_when_projection_differs()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(DifferingClientTag), true, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_does_not_satisfy_IfMatch_when_target_does_not_exist()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), false, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_satisfies_IfMatch_wildcard_when_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch("*", IsWildcard: true), true, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_satisfy_IfMatch_wildcard_when_target_does_not_exist()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch("*", IsWildcard: true), false, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_does_not_satisfy_IfNoneMatch_when_projection_matches_and_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(MatchingClientTag), true, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_satisfies_IfNoneMatch_when_projection_differs_and_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(DifferingClientTag), true, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_satisfy_IfNoneMatch_when_any_list_tag_projection_matches_and_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(
                new WritePrecondition.IfNoneMatch([DifferingClientTag, MatchingClientTag]),
                true,
                CurrentEtag
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_satisfies_IfNoneMatch_when_no_list_tag_projection_matches_and_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(
                new WritePrecondition.IfNoneMatch([DifferingClientTag, "7-other.j._.l.i"]),
                true,
                CurrentEtag
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_satisfies_IfNoneMatch_when_target_does_not_exist()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(MatchingClientTag), false, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_satisfy_IfNoneMatch_wildcard_when_target_exists()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch("*", IsWildcard: true), true, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_satisfies_IfNoneMatch_wildcard_when_target_does_not_exist()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch("*", IsWildcard: true), false, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_satisfy_IfMatch_when_target_is_missing_and_current_etag_is_null()
    {
        // The missing-target path now routes through the evaluator with a null current etag; If-Match
        // against a non-existent target must not be satisfied.
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), targetExists: false, null)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_satisfies_IfNoneMatch_when_target_is_missing_and_current_etag_is_null()
    {
        // If-None-Match against a non-existent target is the create-only success case, even with no
        // current etag to compare.
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(MatchingClientTag), targetExists: false, null)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_reports_no_etag_precondition_for_none()
    {
        RelationalWriteExecutionStateResolver
            .HasEtagPrecondition(new WritePrecondition.None())
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_reports_an_etag_precondition_for_if_match()
    {
        RelationalWriteExecutionStateResolver
            .HasEtagPrecondition(new WritePrecondition.IfMatch(MatchingClientTag))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_reports_an_etag_precondition_for_if_none_match()
    {
        RelationalWriteExecutionStateResolver
            .HasEtagPrecondition(new WritePrecondition.IfNoneMatch(MatchingClientTag))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_rejects_an_unknown_precondition_during_precondition_detection()
    {
        var act = () =>
            RelationalWriteExecutionStateResolver.HasEtagPrecondition(new UnknownWritePrecondition());

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("precondition");
    }

    [Test]
    public void It_rejects_an_unknown_precondition_during_evaluation()
    {
        var act = () =>
            EtagPreconditionEvaluator.IsSatisfied(
                new UnknownWritePrecondition(),
                targetExists: true,
                CurrentEtag
            );

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("precondition");
    }

    private sealed record UnknownWritePrecondition : WritePrecondition;
}
