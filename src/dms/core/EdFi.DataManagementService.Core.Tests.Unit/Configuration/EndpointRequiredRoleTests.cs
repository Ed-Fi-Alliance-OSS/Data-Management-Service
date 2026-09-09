// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
[Parallelizable]
public class Given_EndpointRequiredRole
{
    [TestCase("dms-management-operator")]
    [TestCase("DMS.cache-status:Operator")]
    [TestCase("a")]
    public void It_accepts_a_single_role_token(string requiredRole)
    {
        EndpointRequiredRole.IsValid(requiredRole).Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    public void It_rejects_a_missing_or_empty_value(string? requiredRole)
    {
        EndpointRequiredRole.IsValid(requiredRole).Should().BeFalse();
    }

    [TestCase(" ")]
    [TestCase("dms management operator")]
    [TestCase("dms\toperator")]
    [TestCase("dms-a,dms-b")]
    [TestCase("dms-a;dms-b")]
    [TestCase("\"dms-a\"")]
    [TestCase("'dms-a'")]
    [TestCase("[dms-a]")]
    [TestCase("{dms-a}")]
    [TestCase("dms\u0000a")]
    [TestCase("dms\u007fa")]
    public void It_rejects_whitespace_control_and_delimiter_characters(string requiredRole)
    {
        EndpointRequiredRole.IsValid(requiredRole).Should().BeFalse();
    }

    [Test]
    public void It_accepts_a_value_at_the_maximum_length()
    {
        EndpointRequiredRole.IsValid(new string('a', EndpointRequiredRole.MaximumLength)).Should().BeTrue();
    }

    [Test]
    public void It_rejects_a_value_over_the_maximum_length()
    {
        EndpointRequiredRole
            .IsValid(new string('a', EndpointRequiredRole.MaximumLength + 1))
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_publishes_the_established_maximum_length()
    {
        EndpointRequiredRole.MaximumLength.Should().Be(256);
    }
}
