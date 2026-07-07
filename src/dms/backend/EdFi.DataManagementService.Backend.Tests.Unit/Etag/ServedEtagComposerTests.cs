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
public class Given_ServedEtagComposer
{
    private readonly IServedEtagComposer _sut = new ServedEtagComposer(new EtagComposer());

    [Test]
    public void It_composes_the_same_value_as_the_factory_plus_composer()
    {
        var context = new ServedEtagContext(
            "A1B2C3D4E5",
            ResponseFormat.Json,
            ProfileName: null,
            LinksEnabled: true,
            ContentVersion: 5
        );

        var expected = new EtagComposer().Compose(
            5,
            VariantKeyFactory.Create(
                "A1B2C3D4E5",
                ResponseFormat.Json,
                ProfileVariantCode.Of(null),
                linksEnabled: true
            )
        );

        _sut.Compose(context).Should().Be(expected);
    }

    [Test]
    public void It_derives_the_profile_code_from_the_profile_name()
    {
        var profiled = _sut.Compose(
            new ServedEtagContext(
                "A1B2C3D4E5",
                ResponseFormat.Json,
                "Sample-Profile",
                LinksEnabled: false,
                ContentVersion: 9
            )
        );
        var unprofiled = _sut.Compose(
            new ServedEtagContext(
                "A1B2C3D4E5",
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: false,
                ContentVersion: 9
            )
        );
        profiled.Should().NotBe(unprofiled);
    }
}
