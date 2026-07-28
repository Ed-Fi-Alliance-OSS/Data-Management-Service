// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using FakeItEasy;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public class ClaimsValidatorTests
{
    [TestFixture]
    public class Given_the_schema_fails_to_load
    {
        private const string ResourceLoadFailureMessage =
            "Could not load embedded resource 'test-only' from assembly 'test-only'";

        private Action _act = null!;

        [SetUp]
        public void Setup()
        {
            var validator = new ClaimsValidator(
                A.Fake<ILogger<ClaimsValidator>>(),
                new Lazy<JsonSchema>(() => throw new InvalidOperationException(ResourceLoadFailureMessage))
            );

            _act = () => validator.Validate(JsonNode.Parse("{}")!);
        }

        [Test]
        public void It_propagates_the_invalid_operation_exception_instead_of_returning_a_failure() =>
            _act.Should().Throw<InvalidOperationException>().WithMessage(ResourceLoadFailureMessage);
    }

    [TestFixture]
    public class Given_the_embedded_schema_is_malformed
    {
        private Action _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = () => ClaimsValidator.ParseSchema("{ not valid json", "test-only");
        }

        [Test]
        public void It_surfaces_an_invalid_operation_exception_with_the_json_cause() =>
            _act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("Embedded claims schema 'test-only' is not valid JSON.")
                .WithInnerException<System.Text.Json.JsonException>();
    }
}
