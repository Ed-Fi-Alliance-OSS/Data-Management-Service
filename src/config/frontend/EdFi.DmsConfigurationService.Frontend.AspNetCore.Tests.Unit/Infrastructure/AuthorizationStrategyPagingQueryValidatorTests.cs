// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
public class AuthorizationStrategyPagingQueryValidatorTests
{
    private readonly AuthorizationStrategyPagingQueryValidator _validator = new();

    [Test]
    public void It_allows_an_empty_query()
    {
        var result = _validator.Validate(new FrontendAuthorizationStrategyQuery());
        result.IsValid.Should().BeTrue();
    }

    [TestCase("id")]
    [TestCase("name")]
    [TestCase("displayName")]
    public void It_allows_each_supported_orderBy(string orderBy)
    {
        var query = new FrontendAuthorizationStrategyQuery { OrderBy = orderBy };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_is_case_insensitive_for_orderBy()
    {
        var query = new FrontendAuthorizationStrategyQuery { OrderBy = "DISPLAYNAME" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [TestCase("uri")]
    [TestCase("authorizationStrategyName")]
    public void It_rejects_unsupported_orderBy(string orderBy)
    {
        var query = new FrontendAuthorizationStrategyQuery { OrderBy = orderBy };
        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();

        // Membership only: the allowed-values text is set-built. The client value is never echoed.
        string message = result.Errors[0].ErrorMessage;
        message.Should().StartWith("'orderBy' is not a valid field");
        PagingValidatorMessageAssertions
            .AllowedValuesIn(message)
            .Should()
            .BeEquivalentTo("id", "name", "displayName");
        message.Should().NotContain(orderBy);
    }

    [TestCase("asc")]
    [TestCase("ascending")]
    [TestCase("desc")]
    [TestCase("descending")]
    [TestCase("Ascending")]
    public void It_allows_each_direction_spelling(string direction)
    {
        var query = new FrontendAuthorizationStrategyQuery { OrderBy = "displayName", Direction = direction };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_invalid_direction()
    {
        var query = new FrontendAuthorizationStrategyQuery { Direction = "sideways" };
        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Be("The direction query parameter must be one of: asc, ascending, desc, descending.");
    }

    [Test]
    public void It_rejects_negative_offset()
    {
        var query = new FrontendAuthorizationStrategyQuery { Offset = -1 };
        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Be("'offset' must be greater than or equal to 0.");
    }

    [Test]
    public void It_rejects_zero_limit()
    {
        var query = new FrontendAuthorizationStrategyQuery { Limit = 0 };
        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Be("'limit' must be greater than 0.");
    }

    [Test]
    public void It_maps_all_bound_values_to_the_repository_query()
    {
        var query = new FrontendAuthorizationStrategyQuery
        {
            Offset = 1,
            Limit = 2,
            OrderBy = "displayName",
            Direction = "descending",
        };

        var mapped = query.ToQuery();

        mapped.Offset.Should().Be(1);
        mapped.Limit.Should().Be(2);
        mapped.OrderBy.Should().Be("displayName");
        mapped.Direction.Should().Be("descending");
        mapped.IsDescending.Should().BeTrue();
    }
}
