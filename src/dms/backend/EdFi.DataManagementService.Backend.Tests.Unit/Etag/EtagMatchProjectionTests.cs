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
    public void It_composes_the_current_state_projection_without_representation_components()
    {
        EtagMatchProjection.OfCurrentState(5, "A1B2C3D4FFFFFFFF").Should().Be("5-a1b2c3d4");
    }

    [Test]
    public void It_drops_format_profile_linkFlag_and_contentCoding_keeping_version_and_epoch()
    {
        EtagMatchProjection.Of("5-a1b2c3d4.j._.l.i").Should().Be("5-a1b2c3d4");
        // Same state, different format, profile, link mode, and content coding => same projection.
        EtagMatchProjection.Of("5-a1b2c3d4.x.3.n.g").Should().Be("5-a1b2c3d4");
    }

    [Test]
    public void It_ignores_profile_differences()
    {
        // Amended 2026-07-04: profileCode is no longer state-significant for If-Match, so a profiled
        // and an unprofiled tag for the same ContentVersion/schemaEpoch project equal.
        EtagMatchProjection
            .Of("5-a1b2c3d4.j._.l.i")
            .Should()
            .Be(EtagMatchProjection.Of("5-a1b2c3d4.j.3.l.i"));
    }

    [Test]
    public void It_distinguishes_different_contentVersion_or_schemaEpoch()
    {
        EtagMatchProjection
            .Of("5-a1b2c3d4.j._.l.i")
            .Should()
            .NotBe(EtagMatchProjection.Of("6-a1b2c3d4.j._.l.i"));
        EtagMatchProjection
            .Of("5-a1b2c3d4.j._.l.i")
            .Should()
            .NotBe(EtagMatchProjection.Of("5-ffffffff.j._.l.i"));
    }

    [TestCase("")]
    [TestCase("not-a-tag")]
    [TestCase("5-a1b2c3d4.j._")]
    public void It_yields_a_non_matching_sentinel_for_a_structurally_malformed_tag(string tag)
    {
        EtagMatchProjection.Of(tag).Should().NotBe(EtagMatchProjection.Of("5-a1b2c3d4.j._.l.i"));
    }

    [Test]
    public void It_tolerates_empty_or_unrecognized_content_in_the_ignored_components()
    {
        // The four ignored positions (format, profileCode, linkFlag, contentCoding) are not validated:
        // once a tag has the correct ContentVersion and schemaEpoch and exactly five variantKey parts, it matches
        // whatever those positions hold, including empty. This tolerance is intentional (see
        // EtagMatchProjection remarks) and safe — the significant components must still match. The
        // reviewer's example "5-a1b2c3d4...." is five parts with four empty ignored components.
        EtagMatchProjection.Of("5-a1b2c3d4....").Should().Be("5-a1b2c3d4");
        EtagMatchProjection.Of("5-a1b2c3d4....").Should().Be(EtagMatchProjection.Of("5-a1b2c3d4.j._.n.g"));

        // But a wrong schemaEpoch or ContentVersion in such a tag still fails to match.
        EtagMatchProjection
            .Of("5-ffffffff....")
            .Should()
            .NotBe(EtagMatchProjection.Of("5-a1b2c3d4.j._.n.g"));
        EtagMatchProjection.Of("6-a1b2c3d4....").Should().NotBe(EtagMatchProjection.Of("5-a1b2c3d4.j._.n.g"));
    }
}
