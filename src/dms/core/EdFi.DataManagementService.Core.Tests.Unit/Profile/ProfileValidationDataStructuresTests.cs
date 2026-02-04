// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileValidationDataStructuresTests
{
    public class Given_ValidationSeverity
    {
        [Test]
        public void Should_have_Error_and_Warning_values()
        {
            // Arrange & Act & Assert
            ValidationSeverity.Error.Should().Be(ValidationSeverity.Error);
            ValidationSeverity.Warning.Should().Be(ValidationSeverity.Warning);
        }
    }

    public class Given_ValidationFailure
    {
        [Test]
        public void Should_construct_with_all_properties()
        {
            // Arrange
            var severity = ValidationSeverity.Error;
            var profileName = "TestProfile";
            var resourceName = "Student";
            var memberName = "firstName";
            var message = "Invalid member";

            // Act
            var failure = new ValidationFailure(severity, profileName, resourceName, memberName, message);

            // Assert
            failure.Severity.Should().Be(severity);
            failure.ProfileName.Should().Be(profileName);
            failure.ResourceName.Should().Be(resourceName);
            failure.MemberName.Should().Be(memberName);
            failure.Message.Should().Be(message);
        }

        [Test]
        public void Should_allow_null_resource_and_member_names()
        {
            // Arrange
            var severity = ValidationSeverity.Warning;
            var profileName = "TestProfile";
            string? resourceName = null;
            string? memberName = null;
            var message = "Profile level issue";

            // Act
            var failure = new ValidationFailure(severity, profileName, resourceName, memberName, message);

            // Assert
            failure.Severity.Should().Be(severity);
            failure.ProfileName.Should().Be(profileName);
            failure.ResourceName.Should().BeNull();
            failure.MemberName.Should().BeNull();
            failure.Message.Should().Be(message);
        }
    }

    public class Given_ProfileValidationResult
    {
        [Test]
        public void Should_construct_with_empty_failures_list()
        {
            // Arrange
            var failures = new List<ValidationFailure>();

            // Act
            var result = new ProfileValidationResult(failures);

            // Assert
            result.Failures.Should().BeEmpty();
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeFalse();
            result.IsValid.Should().BeTrue();
        }

        [Test]
        public void Should_construct_with_error_failures()
        {
            // Arrange
            var failures = new List<ValidationFailure>
            {
                new(
                    ValidationSeverity.Error,
                    "TestProfile",
                    "Student",
                    "invalidField",
                    "Field does not exist"
                ),
            };

            // Act
            var result = new ProfileValidationResult(failures);

            // Assert
            result.Failures.Should().HaveCount(1);
            result.HasErrors.Should().BeTrue();
            result.HasWarnings.Should().BeFalse();
            result.IsValid.Should().BeFalse();
        }

        [Test]
        public void Should_construct_with_warning_failures()
        {
            // Arrange
            var failures = new List<ValidationFailure>
            {
                new(ValidationSeverity.Warning, "TestProfile", "Student", "id", "Identifying field excluded"),
            };

            // Act
            var result = new ProfileValidationResult(failures);

            // Assert
            result.Failures.Should().HaveCount(1);
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeTrue();
            result.IsValid.Should().BeFalse();
        }

        [Test]
        public void Should_construct_with_mixed_failures()
        {
            // Arrange
            var failures = new List<ValidationFailure>
            {
                new(
                    ValidationSeverity.Error,
                    "TestProfile",
                    "Student",
                    "invalidField",
                    "Field does not exist"
                ),
                new(ValidationSeverity.Warning, "TestProfile", "Student", "id", "Identifying field excluded"),
            };

            // Act
            var result = new ProfileValidationResult(failures);

            // Assert
            result.Failures.Should().HaveCount(2);
            result.HasErrors.Should().BeTrue();
            result.HasWarnings.Should().BeTrue();
            result.IsValid.Should().BeFalse();
        }

        [Test]
        public void Success_should_return_valid_result()
        {
            // Act
            var result = ProfileValidationResult.Success;

            // Assert
            result.Failures.Should().BeEmpty();
            result.HasErrors.Should().BeFalse();
            result.HasWarnings.Should().BeFalse();
            result.IsValid.Should().BeTrue();
        }

        [Test]
        public void Failure_with_single_failure_should_create_result()
        {
            // Arrange
            var failure = new ValidationFailure(
                ValidationSeverity.Error,
                "TestProfile",
                null,
                null,
                "Invalid profile"
            );

            // Act
            var result = ProfileValidationResult.Failure(failure);

            // Assert
            result.Failures.Should().ContainSingle();
            result.Failures[0].Should().Be(failure);
            result.HasErrors.Should().BeTrue();
            result.IsValid.Should().BeFalse();
        }

        [Test]
        public void Failure_with_multiple_failures_should_create_result()
        {
            // Arrange
            var failures = new List<ValidationFailure>
            {
                new(
                    ValidationSeverity.Error,
                    "TestProfile",
                    "Student",
                    "invalidField",
                    "Field does not exist"
                ),
                new(ValidationSeverity.Warning, "TestProfile", "Student", "id", "Identifying field excluded"),
            };

            // Act
            var result = ProfileValidationResult.Failure(failures);

            // Assert
            result.Failures.Should().HaveCount(2);
            result.HasErrors.Should().BeTrue();
            result.HasWarnings.Should().BeTrue();
            result.IsValid.Should().BeFalse();
        }
    }
}
