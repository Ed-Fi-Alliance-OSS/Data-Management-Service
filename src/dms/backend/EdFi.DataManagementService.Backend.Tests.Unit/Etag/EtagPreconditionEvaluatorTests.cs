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
    private const string CurrentEtag = "5-a1b2c3d4.j._.l";
    private const string MatchingClientTag = "5-a1b2c3d4.x.3.n";
    private const string DifferingClientTag = "6-a1b2c3d4.j._.l";

    [TestCase(true)]
    [TestCase(false)]
    public void None_is_always_satisfied(bool targetExists)
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.None(), targetExists, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void IfMatch_with_matching_projection_and_existing_target_is_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), true, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void IfMatch_with_differing_projection_is_not_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(DifferingClientTag), true, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void IfMatch_when_target_does_not_exist_is_not_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), false, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void IfMatch_wildcard_with_existing_target_is_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch("*", IsWildcard: true), true, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void IfMatch_wildcard_when_target_does_not_exist_is_not_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch("*", IsWildcard: true), false, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void IfNoneMatch_with_matching_projection_and_existing_target_is_not_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(MatchingClientTag), true, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void IfNoneMatch_with_differing_projection_and_existing_target_is_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(DifferingClientTag), true, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void IfNoneMatch_when_target_does_not_exist_is_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(MatchingClientTag), false, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void IfNoneMatch_wildcard_with_existing_target_is_not_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch("*", IsWildcard: true), true, CurrentEtag)
            .Should()
            .BeFalse();
    }

    [Test]
    public void IfNoneMatch_wildcard_when_target_does_not_exist_is_satisfied()
    {
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch("*", IsWildcard: true), false, CurrentEtag)
            .Should()
            .BeTrue();
    }

    [Test]
    public void IfMatch_with_a_missing_target_and_null_current_etag_is_not_satisfied()
    {
        // The missing-target path now routes through the evaluator with a null current etag; If-Match
        // against a non-existent target must not be satisfied.
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfMatch(MatchingClientTag), targetExists: false, null)
            .Should()
            .BeFalse();
    }

    [Test]
    public void IfNoneMatch_with_a_missing_target_and_null_current_etag_is_satisfied()
    {
        // If-None-Match against a non-existent target is the create-only success case, even with no
        // current etag to compare.
        EtagPreconditionEvaluator
            .IsSatisfied(new WritePrecondition.IfNoneMatch(MatchingClientTag), targetExists: false, null)
            .Should()
            .BeTrue();
    }
}
