// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Validation;

[TestFixture]
[SetCulture("en-US")]
public class Given_DecimalValidator
{
    [TestCase("999.99", 5, 2)]
    [TestCase("-999.99", 5, 2)]
    [TestCase("999.989", 5, 2)]
    [TestCase("-999.989", 5, 2)]
    [TestCase("9999", 4, 0)]
    [TestCase("-9999", 4, 0)]
    [TestCase("0.999", 3, 3)]
    [TestCase("-0.999", 3, 3)]
    [TestCase("999.9", 4, 1)]
    [TestCase("-999.9", 4, 1)]
    [TestCase("0", 5, 2)]
    public void It_accepts_the_inclusive_range(string value, short totalDigits, short decimalPlaces)
    {
        // This validator enforces the range, not a separate fractional digit count.
        var errors = new DecimalValidator().Validate(
            JsonNode.Parse("{\"value\":" + value + "}")!,
            [new(new("$.value"), totalDigits, decimalPlaces)]
        );

        errors.Should().BeEmpty();
    }

    [TestCase("999.991", 5, 2, "value must be between -999.99 and 999.99.")]
    [TestCase("-999.991", 5, 2, "value must be between -999.99 and 999.99.")]
    [TestCase("10000", 4, 0, "value must be between -9999 and 9999.")]
    [TestCase("-10000", 4, 0, "value must be between -9999 and 9999.")]
    [TestCase("1", 3, 3, "value must be between -0.999 and 0.999.")]
    [TestCase("-1", 3, 3, "value must be between -0.999 and 0.999.")]
    [TestCase("999.91", 4, 1, "value must be between -999.9 and 999.9.")]
    [TestCase("-999.91", 4, 1, "value must be between -999.9 and 999.9.")]
    public void It_rejects_values_immediately_outside_the_range(
        string value,
        short totalDigits,
        short decimalPlaces,
        string expectedError
    )
    {
        var errors = new DecimalValidator().Validate(
            JsonNode.Parse("{\"value\":" + value + "}")!,
            [new(new("$.value"), totalDigits, decimalPlaces)]
        );

        errors.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["$.value"] = [expectedError] });
    }

    [TestCase("null")]
    [TestCase("\"1.2\"")]
    [TestCase("true")]
    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("1e400")]
    [TestCase("-1e400")]
    public void It_reports_an_unusable_decimal_without_throwing(string value)
    {
        var errors = new DecimalValidator().Validate(
            JsonNode.Parse("{\"value\":" + value + "}")!,
            [new(new("$.value"), 5, 2)]
        );

        errors
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, string[]> { ["$.value"] = ["Value is not a valid decimal."] }
            );
    }

    [TestCase(null, null)]
    [TestCase(5, null)]
    [TestCase(null, 2)]
    public void It_does_not_invent_a_range_when_either_limit_is_unspecified(
        short? totalDigits,
        short? decimalPlaces
    )
    {
        var errors = new DecimalValidator().Validate(
            JsonNode.Parse("""{"value":100000} """)!,
            [new(new("$.value"), totalDigits, decimalPlaces)]
        );

        errors.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_require_an_absent_optional_property()
    {
        var errors = new DecimalValidator().Validate(
            JsonNode.Parse("""{"other":100000,"items":[]} """)!,
            [new(new("$.value"), 5, 2), new(new("$.items[*].value"), 5, 2)]
        );

        errors.Should().BeEmpty();
    }

    [Test]
    public void It_reports_every_invalid_match_under_its_schema_path()
    {
        var errors = new DecimalValidator().Validate(
            JsonNode.Parse(
                """
                {"items":[{"value":1000},{"value":999.99},{"value":-1000},{"value":null}],"other":10}
                """
            )!,
            [new(new("$.items[*].value"), 5, 2), new(new("$.other"), 1, 0)]
        );

        errors.Keys.Should().BeEquivalentTo("$.items[*].value", "$.other");
        errors["$.items[*].value"]
            .Should()
            .Equal(
                "items[*].value must be between -999.99 and 999.99.",
                "items[*].value must be between -999.99 and 999.99.",
                "Value is not a valid decimal."
            );
        errors["$.other"].Should().Equal("other must be between -9 and 9.");
    }
}
