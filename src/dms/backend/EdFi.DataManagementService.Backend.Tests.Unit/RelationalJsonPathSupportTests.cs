// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalJsonPathSupport
{
    [Test]
    public void It_should_round_trip_restricted_canonical_paths_without_precomputed_segments()
    {
        var path = new JsonPathExpression("$.sections[*].sessions[*]", []);

        var segments = RelationalJsonPathSupport.GetRestrictedSegments(path);
        var canonicalPath = RelationalJsonPathSupport.BuildCanonical(segments);

        canonicalPath.Should().Be("$.sections[*].sessions[*]");
    }

    [Test]
    public void It_should_combine_restricted_paths_using_precomputed_and_parsed_segments()
    {
        var scopePath = new JsonPathExpression(
            "$.sections[*]",
            [new JsonPathSegment.Property("sections"), new JsonPathSegment.AnyArrayElement()]
        );
        var relativePath = new JsonPathExpression("$._ext.sample", []);

        var combinedPath = RelationalJsonPathSupport.CombineRestrictedCanonical(scopePath, relativePath);

        combinedPath.Should().Be("$.sections[*]._ext.sample");
    }

    [Test]
    public void It_should_extract_wildcard_and_ordinal_paths_from_concrete_reference_paths()
    {
        var parsedPath = RelationalJsonPathSupport.ParseConcretePath(
            new JsonPath("$.sections[2].sessions[5].schoolReference")
        );

        parsedPath.WildcardPath.Should().Be("$.sections[*].sessions[*].schoolReference");
        parsedPath.OrdinalPath.Should().Equal(2, 5);
    }

    [Test]
    public void It_should_reject_non_canonical_concrete_reference_paths()
    {
        var act = () =>
            RelationalJsonPathSupport.ParseConcretePath(new JsonPath("$.sections[*].schoolReference"));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Resolved reference path '$.sections[*].schoolReference' is not a canonical JSONPath."
            );
    }
}
