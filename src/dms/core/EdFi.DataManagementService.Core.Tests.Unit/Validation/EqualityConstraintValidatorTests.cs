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
public class Given_EqualityConstraintValidator
{
    [TestCase("{}")]
    [TestCase("""{"values":[]} """)]
    [TestCase("""{"expected":1} """)]
    [TestCase("""{"values":[1,1]} """)]
    [TestCase("""{"values":[1,1],"expected":1} """)]
    public void It_accepts_zero_or_one_distinct_value_across_both_paths(string document)
    {
        var errors = new EqualityConstraintValidator().Validate(
            JsonNode.Parse(document),
            [new(new("$.values[*]"), new("$.expected"))]
        );

        errors.Should().BeEmpty();
    }

    [Test]
    public void It_reports_conflicting_array_values_even_when_the_other_path_is_absent()
    {
        var errors = new EqualityConstraintValidator().Validate(
            JsonNode.Parse("""{"values":[1,2,1]} """),
            [new(new("$.values[*]"), new("$.expected"))]
        );

        errors
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, string[]>
                {
                    ["$.values[*]"] = [Message("[*]", "'1', '2'")],
                    ["$.expected"] = [Message("expected", "'1', '2'")],
                }
            );
    }

    [Test]
    public void It_accumulates_errors_from_multiple_constraints_sharing_one_path()
    {
        var errors = new EqualityConstraintValidator().Validate(
            JsonNode.Parse("""{"first":{"schoolId":1},"second":{"schoolId":2},"third":{"schoolId":3}}"""),
            [
                new(new("$.first.schoolId"), new("$.second.schoolId")),
                new(new("$.first.schoolId"), new("$.third.schoolId")),
            ]
        );

        errors.Keys.Should().BeEquivalentTo("$.first.schoolId", "$.second.schoolId", "$.third.schoolId");
        errors["$.first.schoolId"]
            .Should()
            .Equal(Message("schoolId", "'1', '2'"), Message("schoolId", "'1', '3'"));
        errors["$.second.schoolId"].Should().Equal(Message("schoolId", "'1', '2'"));
        errors["$.third.schoolId"].Should().Equal(Message("schoolId", "'1', '3'"));
    }

    private static string Message(string field, string conflictingValues) =>
        $"All values supplied for '{field}' must match."
        + " Review all references (including those higher up in the resource's data)"
        + $" and align the following conflicting values: {conflictingValues}";
}
