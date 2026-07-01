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
public class Given_ProfileVariantCodeRegistry
{
    [Test]
    public void It_maps_no_profile_to_underscore()
    {
        var registry = new ProfileVariantCodeRegistry(["b-profile", "a-profile"]);

        registry.CodeFor(null).Should().Be("_");
    }

    [Test]
    public void It_assigns_stable_ordinal_codes_independent_of_input_order()
    {
        var first = new ProfileVariantCodeRegistry(["b-profile", "a-profile"]);
        var second = new ProfileVariantCodeRegistry(["a-profile", "b-profile"]);

        first.CodeFor("a-profile").Should().Be("0");
        first.CodeFor("b-profile").Should().Be("1");
        second.CodeFor("a-profile").Should().Be("0");
        second.CodeFor("b-profile").Should().Be("1");
    }

    [Test]
    public void It_throws_for_an_unknown_profile()
    {
        var registry = new ProfileVariantCodeRegistry(["a-profile"]);

        Action act = () => registry.CodeFor("missing");

        act.Should().Throw<KeyNotFoundException>();
    }
}
