// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Etag;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Etag;

[TestFixture]
[Parallelizable]
public partial class Given_ProfileVariantCode
{
    [Test]
    public void It_maps_no_profile_to_underscore()
    {
        ProfileVariantCode.Of(null).Should().Be("_");
    }

    [Test]
    public void It_is_stable_for_the_same_name()
    {
        ProfileVariantCode.Of("ReadableProfile").Should().Be(ProfileVariantCode.Of("ReadableProfile"));
    }

    [Test]
    public void It_differs_for_different_names()
    {
        ProfileVariantCode.Of("ProfileA").Should().NotBe(ProfileVariantCode.Of("ProfileB"));
    }

    [Test]
    public void It_produces_eight_lowercase_hex_characters()
    {
        var code = ProfileVariantCode.Of("ReadableProfile");

        HexPrefix().IsMatch(code).Should().BeTrue();
    }

    [GeneratedRegex("^[0-9a-f]{8}$")]
    private static partial Regex HexPrefix();
}
