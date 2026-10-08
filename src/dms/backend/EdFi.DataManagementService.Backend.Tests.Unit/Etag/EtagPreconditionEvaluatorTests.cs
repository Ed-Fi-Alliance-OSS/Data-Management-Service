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

    [Test]
    public void It_always_satisfies_when_no_precondition()
    {
        EtagPreconditionEvaluator.IsSatisfied(new WritePrecondition.None(), CurrentEtag).Should().BeTrue();
    }

    [Test]
    public void It_satisfies_IfMatch_when_projection_matches()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), CurrentEtag)
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
            .IsSatisfied(new WritePrecondition.IfMatch(DifferingClientTag), CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_satisfies_IfMatch_wildcard()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch("*", IsWildcard: true), CurrentEtag)
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
    public void It_rejects_an_unknown_precondition_during_precondition_detection()
    {
        var act = () =>
            RelationalWriteExecutionStateResolver.HasEtagPrecondition(new UnknownWritePrecondition());

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("precondition");
    }

    [Test]
    public void It_rejects_an_unknown_precondition_during_evaluation()
    {
        var act = () => EtagPreconditionEvaluator.IsSatisfied(new UnknownWritePrecondition(), CurrentEtag);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("precondition");
    }

    private sealed record UnknownWritePrecondition : WritePrecondition;
}
