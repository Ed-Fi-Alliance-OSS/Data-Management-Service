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

        key.Value.Should().Be("a1b2c3d4.j._.l");
    }

    [Test]
    public void It_uses_profile_code_and_links_off()
    {
        var key = VariantKeyFactory.Create("a1b2c3d4e5f6", ResponseFormat.Json, "3", linksEnabled: false);

        key.Value.Should().Be("a1b2c3d4.j.3.n");
    }

    [Test]
    public void It_derives_schemaEpoch_as_first_8_lowercase_hex()
    {
        var key = VariantKeyFactory.Create("A1B2C3D4FFFF", ResponseFormat.Json, "_", linksEnabled: true);

        key.Value.Should().StartWith("a1b2c3d4.");
    }
}
