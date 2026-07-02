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
public class DescriptorVariantKeyTests
{
    [Test]
    public void For_composes_no_profile_no_links_json_variantKey()
    {
        DescriptorVariantKey.For("a1b2c3d4e5f6").Value.Should().Be("a1b2c3d4.j._.n");
    }

    [Test]
    public void For_lowercases_and_truncates_the_schema_epoch()
    {
        DescriptorVariantKey.For("A1B2C3D4FFFF").Value.Should().StartWith("a1b2c3d4.");
    }
}
