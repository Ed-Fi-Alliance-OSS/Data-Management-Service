// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileDataValidatorTests
{
    public class Given_ProfileDataValidator
    {
        private ILogger<ProfileDataValidator> _logger = null!;
        private IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = null!;

        [SetUp]
        public void Setup()
        {
            _logger = A.Fake<ILogger<ProfileDataValidator>>();
            _effectiveApiSchemaProvider = A.Fake<IEffectiveApiSchemaProvider>();
        }

        [Test]
        public void Should_construct_successfully()
        {
            // Act
            var validator = new ProfileDataValidator(_logger);

            // Assert
            validator.Should().NotBeNull();
        }

        [Test]
        public void Validate_should_return_success_with_no_failures_initially()
        {
            // Arrange
            var validator = new ProfileDataValidator(_logger);
            var profileDefinition = new ProfileDefinition("TestProfile", []);

            // Act
            var result = validator.Validate(profileDefinition, _effectiveApiSchemaProvider);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeTrue();
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeFalse();
            result.Failures.Should().BeEmpty();
        }
    }
}
