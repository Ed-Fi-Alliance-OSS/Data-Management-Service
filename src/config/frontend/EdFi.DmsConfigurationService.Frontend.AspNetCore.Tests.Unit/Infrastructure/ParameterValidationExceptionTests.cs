// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
public class Given_A_Parameter_Validation_Exception_With_Messages
{
    private ParameterValidationException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = new ParameterValidationException([
            "'limit' must be greater than 0.",
            "'orderBy' is not a valid field.",
        ]);
    }

    [Test]
    public void It_carries_the_supplied_messages_as_an_immutable_array()
    {
        _exception.Errors.Should().BeOfType<ImmutableArray<string>>();
    }

    [Test]
    public void It_preserves_the_supplied_messages_and_order()
    {
        _exception
            .Errors.Should()
            .Equal("'limit' must be greater than 0.", "'orderBy' is not a valid field.");
    }
}

[TestFixture]
public class Given_A_Parameter_Validation_Exception_Constructed_With_Various_Inputs
{
    [Test]
    public void It_throws_ArgumentNullException_when_messages_are_null()
    {
        Action act = () => _ = new ParameterValidationException(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_produces_an_empty_immutable_array_for_an_empty_collection()
    {
        var exception = new ParameterValidationException([]);

        exception.Errors.IsDefaultOrEmpty.Should().BeTrue();
    }

    [Test]
    public void It_does_not_derive_from_fluent_validation_exception()
    {
        // Guards the classification: a FluentValidation.ValidationException maps to bad-request:data,
        // so the parameter exception must NOT derive from it.
        typeof(FluentValidation.ValidationException)
            .IsAssignableFrom(typeof(ParameterValidationException))
            .Should()
            .BeFalse();
    }
}
