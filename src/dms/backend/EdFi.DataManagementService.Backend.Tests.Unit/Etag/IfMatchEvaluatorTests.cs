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
public class Given_IfMatchEvaluator
{
    private readonly IIfMatchEvaluator _sut = new IfMatchEvaluator();
    private const string Current = "5-a1b2c3d4.j._.l";

    [Test]
    public void It_matches_a_wildcard_regardless_of_value()
    {
        // Construct the wildcard the same way WritePreconditionFactory.Create does for a bare "*".
        _sut.Evaluate(new WritePrecondition.IfMatch("*", IsWildcard: true), Current)
            .IsMatch.Should()
            .BeTrue();
    }

    [Test]
    public void It_matches_when_the_state_significant_projection_is_equal()
    {
        // Different format/profile/link, same version+epoch.
        _sut.Evaluate(new WritePrecondition.IfMatch("5-a1b2c3d4.x.3.n"), Current).IsMatch.Should().BeTrue();
    }

    [Test]
    public void It_does_not_match_a_different_version_or_epoch()
    {
        _sut.Evaluate(new WritePrecondition.IfMatch("6-a1b2c3d4.j._.l"), Current).IsMatch.Should().BeFalse();
        _sut.Evaluate(new WritePrecondition.IfMatch("5-ffffffff.j._.l"), Current).IsMatch.Should().BeFalse();
    }
}
