// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Etag;

[TestFixture]
[Parallelizable]
public class Given_EtagMatchProjection
{
    [Test]
    public void It_drops_format_profile_and_linkFlag_keeping_version_and_epoch()
    {
        EtagMatchProjection.Of("5-a1b2c3d4.j._.l").Should().Be("5-a1b2c3d4");
        // Same state, different format, profile, and link mode => same projection.
        EtagMatchProjection.Of("5-a1b2c3d4.x.3.n").Should().Be("5-a1b2c3d4");
    }

    [Test]
    public void It_ignores_profile_differences()
    {
        // Amended 2026-07-04: profileCode is no longer state-significant for If-Match, so a profiled
        // and an unprofiled tag for the same ContentVersion/schemaEpoch project equal.
        EtagMatchProjection.Of("5-a1b2c3d4.j._.l").Should().Be(EtagMatchProjection.Of("5-a1b2c3d4.j.3.l"));
    }

    [Test]
    public void It_distinguishes_different_contentVersion_or_schemaEpoch()
    {
        EtagMatchProjection.Of("5-a1b2c3d4.j._.l").Should().NotBe(EtagMatchProjection.Of("6-a1b2c3d4.j._.l"));
        EtagMatchProjection.Of("5-a1b2c3d4.j._.l").Should().NotBe(EtagMatchProjection.Of("5-ffffffff.j._.l"));
    }

    [TestCase("")]
    [TestCase("not-a-tag")]
    [TestCase("5-a1b2c3d4.j._")]
    public void It_yields_a_non_matching_sentinel_for_a_malformed_tag(string tag)
    {
        EtagMatchProjection.Of(tag).Should().NotBe(EtagMatchProjection.Of("5-a1b2c3d4.j._.l"));
    }
}
