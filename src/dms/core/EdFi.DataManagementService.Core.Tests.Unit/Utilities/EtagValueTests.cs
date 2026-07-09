// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixture]
[Parallelizable]
public class Given_EtagValue
{
    [Test]
    public void It_composes_contentVersion_and_variantKey_with_hyphen()
    {
        EtagValue.Compose("5", "a1b2c3d4.j._.l").Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void It_wraps_header_value_in_double_quotes()
    {
        EtagValue.ToHeaderValue("5-a1b2c3d4.j._.l").Should().Be("\"5-a1b2c3d4.j._.l\"");
    }

    [Test]
    public void It_parses_a_quoted_header_value()
    {
        EtagValue.TryParseHeaderValue("\"5-a1b2c3d4.j._.l\"", out var value).Should().BeTrue();
        value.Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void It_accepts_an_unquoted_header_value_for_backward_tolerance()
    {
        EtagValue.TryParseHeaderValue("5-a1b2c3d4.j._.l", out var value).Should().BeTrue();
        value.Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void It_rejects_a_weak_validator()
    {
        EtagValue.TryParseHeaderValue("W/\"5-x\"", out _).Should().BeFalse();
    }

    [Test]
    public void It_rejects_a_null_or_empty_header_value()
    {
        EtagValue.TryParseHeaderValue(null, out _).Should().BeFalse();
        EtagValue.TryParseHeaderValue(string.Empty, out _).Should().BeFalse();
    }

    [Test]
    public void It_round_trips_compose_and_parse()
    {
        var etag = EtagValue.Compose("5", "a1b2c3d4.j._.l");
        EtagValue.TryParse(etag, out var contentVersion, out var variantKey).Should().BeTrue();
        contentVersion.Should().Be("5");
        variantKey.Should().Be("a1b2c3d4.j._.l");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("-a1b2c3d4.j._.l")] // empty content version
    [TestCase("5-")] // empty variant key
    [TestCase("nodash")]
    public void It_rejects_malformed_values(string? value)
    {
        EtagValue.TryParse(value, out var contentVersion, out var variantKey).Should().BeFalse();
        contentVersion.Should().BeEmpty();
        variantKey.Should().BeEmpty();
    }
}
