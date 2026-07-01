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
public class Given_EtagComposer
{
    [Test]
    public void It_produces_contentVersion_dash_variantKey()
    {
        var composer = new EtagComposer();

        composer.Compose(5, new VariantKey("a1b2c3d4.j._.l")).Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void It_serializes_contentVersion_as_a_full_string()
    {
        var composer = new EtagComposer();

        composer
            .Compose(9007199254740993, new VariantKey("a1b2c3d4.j._.l"))
            .Should()
            .Be("9007199254740993-a1b2c3d4.j._.l");
    }
}
