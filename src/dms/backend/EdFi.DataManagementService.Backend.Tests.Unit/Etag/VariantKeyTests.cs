// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Etag;

[TestFixture]
[Parallelizable]
public class Given_VariantKeyFactory
{
    [Test]
    public void It_formats_json_no_profile_links_on()
    {
        var key = VariantKeyFactory.Create(
            effectiveSchemaHash: "a1b2c3d4e5f6",
            format: ResponseFormat.Json,
            profileCode: VariantKey.NoProfileCode,
            linksEnabled: true
        );

        key.Value.Should().Be("a1b2c3d4.j._.l.i");
    }

    [Test]
    public void It_uses_profile_code_and_links_off()
    {
        var key = VariantKeyFactory.Create("a1b2c3d4e5f6", ResponseFormat.Json, "3", linksEnabled: false);

        key.Value.Should().Be("a1b2c3d4.j.3.n.i");
    }

    [Test]
    public void It_derives_schemaEpoch_as_first_8_lowercase_hex()
    {
        var key = VariantKeyFactory.Create("A1B2C3D4FFFF", ResponseFormat.Json, "_", linksEnabled: true);

        key.Value.Should().StartWith("a1b2c3d4.");
    }

    [TestCase(ResponseContentCoding.Identity, "i")]
    [TestCase(ResponseContentCoding.Brotli, "b")]
    [TestCase(ResponseContentCoding.Gzip, "g")]
    public void It_uses_a_stable_content_coding_code(ResponseContentCoding contentCoding, string expectedCode)
    {
        var key = VariantKeyFactory.Create(
            "a1b2c3d4e5f6",
            ResponseFormat.Json,
            VariantKey.NoProfileCode,
            linksEnabled: true,
            contentCoding
        );

        key.Value.Should().Be($"a1b2c3d4.j._.l.{expectedCode}");
    }
}

[TestFixture]
[Parallelizable]
public class Given_VariantKey
{
    [Test]
    public void It_parses_a_well_formed_key_into_components()
    {
        new VariantKey("a1b2c3d4.j._.l.g").TryParseComponents(out var components).Should().BeTrue();
        components.SchemaEpoch.Should().Be("a1b2c3d4");
        components.Format.Should().Be("j");
        components.ProfileCode.Should().Be("_");
        components.LinkFlag.Should().Be("l");
        components.ContentCoding.Should().Be("g");
        components.IfMatchSignificant().Should().Be("a1b2c3d4");
    }

    [TestCase("a1b2c3d4.j._")] // 3 parts
    [TestCase("a1b2c3d4.j._.l")] // 4 parts
    [TestCase("a1b2c3d4.j._.l.g.extra")] // 6 parts
    [TestCase("")]
    public void It_rejects_keys_without_exactly_five_components(string value)
    {
        new VariantKey(value).TryParseComponents(out _).Should().BeFalse();
    }

    [Test]
    public void It_formats_components_back_into_the_wire_value()
    {
        VariantKey.FromComponents("a1b2c3d4", "j", "_", "l", "g").Value.Should().Be("a1b2c3d4.j._.l.g");
    }
}
