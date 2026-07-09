// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class WritePreconditionFactoryTests
{
    [Test]
    public void It_returns_none_when_if_match_is_absent()
    {
        var result = WritePreconditionFactory.Create(new Dictionary<string, string>());

        result.Should().BeOfType<WritePrecondition.None>();
    }

    [TestCase("\"5-a1b2c3d4.j._.l\"", "5-a1b2c3d4.j._.l")] // quoted strong validator -> unquoted opaque tag
    [TestCase("5-a1b2c3d4.j._.l", "5-a1b2c3d4.j._.l")] // bare unquoted value tolerated
    [TestCase("plain-opaque-value", "plain-opaque-value")]
    public void It_normalizes_the_if_match_to_the_unquoted_opaque_tag(
        string ifMatchValue,
        string expectedValue
    )
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["If-Match"] = ifMatchValue,
        };

        var result = WritePreconditionFactory.Create(headers);

        result.Should().Be(new WritePrecondition.IfMatch(expectedValue));
    }

    [TestCase("W/\"5-a1b2c3d4.j._.l\"")] // weak validators must not be used with If-Match (RFC 7232 §3.1)
    public void It_keeps_a_weak_if_match_verbatim_so_it_cannot_match_a_current_tag(string ifMatchValue)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["If-Match"] = ifMatchValue,
        };

        var result = WritePreconditionFactory.Create(headers);

        // A weak tag is not accepted; keeping it verbatim (unquoted-normalization skipped) means the
        // backend's state-significant projection cannot equal a well-formed current tag -> 412.
        result.Should().Be(new WritePrecondition.IfMatch(ifMatchValue));
    }

    [Test]
    public void It_produces_a_wildcard_for_a_bare_asterisk()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["If-Match"] = "*" };

        var result = WritePreconditionFactory.Create(headers);

        result.Should().Be(new WritePrecondition.IfMatch("*", IsWildcard: true));
    }

    [Test]
    public void It_does_not_treat_a_quoted_asterisk_as_a_wildcard()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["If-Match"] = "\"*\"",
        };

        var result = WritePreconditionFactory.Create(headers);

        result.Should().Be(new WritePrecondition.IfMatch("*")); // IsWildcard defaults false
    }
}
