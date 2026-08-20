// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using FluentValidation;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
public class ValidatorExtensionsTests
{
    [Test]
    public void GuardRouteIdMatchesBodyId_throws_when_ids_do_not_match()
    {
        Action act = () => ValidatorExtensions.GuardRouteIdMatchesBodyId(1, 2);

        var assertion = act.Should().Throw<ValidationException>();
        var errors = assertion.Which.Errors.ToList();
        errors.Should().ContainSingle();
        errors[0].PropertyName.Should().Be("Id");
        errors[0].ErrorMessage.Should().Be("Request body id must match the id in the url.");
    }

    [Test]
    public void GuardRouteIdMatchesBodyId_throws_when_body_id_is_omitted()
    {
        Action act = () => ValidatorExtensions.GuardRouteIdMatchesBodyId(1, 0);

        var assertion = act.Should().Throw<ValidationException>();
        var errors = assertion.Which.Errors.ToList();
        errors.Should().ContainSingle();
        errors[0].PropertyName.Should().Be("Id");
        errors[0].ErrorMessage.Should().Be("Request body id must match the id in the url.");
    }

    [Test]
    public void GuardRouteIdMatchesBodyId_succeeds_when_ids_match()
    {
        Action act = () => ValidatorExtensions.GuardRouteIdMatchesBodyId(1, 1);

        act.Should().NotThrow();
    }

    [Test]
    public void GuardRouteIdMatchesBodyId_succeeds_when_both_ids_are_zero()
    {
        Action act = () => ValidatorExtensions.GuardRouteIdMatchesBodyId(0, 0);

        act.Should().NotThrow();
    }
}
