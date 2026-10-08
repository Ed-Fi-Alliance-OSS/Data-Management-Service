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
public class ActionPagingQueryValidatorTests
{
    private readonly ActionPagingQueryValidator _validator = new();

    [Test]
    public void It_allows_an_empty_query()
    {
        var result = _validator.Validate(new FrontendActionQuery());
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_filters_without_paging()
    {
        var query = new FrontendActionQuery { Id = 2, Name = "Read" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_id()
    {
        var query = new FrontendActionQuery { OrderBy = "id" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_name()
    {
        var query = new FrontendActionQuery { OrderBy = "name" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_is_case_insensitive_for_orderBy()
    {
        var query = new FrontendActionQuery { OrderBy = "NAME" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_orderBy_uri()
    {
        var query = new FrontendActionQuery { OrderBy = "uri" };
        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();

        // The allowed-values text is built from a set, so only its membership is asserted, never its
        // ordering. The client-supplied value must not be echoed back.
        string message = result.Errors[0].ErrorMessage;
        message.Should().StartWith("'orderBy' is not a valid field");
        PagingValidatorMessageAssertions.AllowedValuesIn(message).Should().BeEquivalentTo("id", "name");
        message.Should().NotContain("uri");
    }

    [TestCase("asc")]
    [TestCase("ascending")]
    [TestCase("desc")]
    [TestCase("descending")]
    [TestCase("DESC")]
    public void It_allows_each_direction_spelling(string direction)
    {
        var query = new FrontendActionQuery { OrderBy = "id", Direction = direction };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_invalid_direction()
    {
        var query = new FrontendActionQuery { Direction = "sideways" };
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
        var query = new FrontendActionQuery { Offset = -1 };
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
        var query = new FrontendActionQuery { Limit = 0 };
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
        var query = new FrontendActionQuery
        {
            Id = 3,
            Name = "Update",
            Offset = 1,
            Limit = 2,
            OrderBy = "name",
            Direction = "desc",
        };

        var mapped = query.ToQuery();

        mapped.Id.Should().Be(3);
        mapped.Name.Should().Be("Update");
        mapped.Offset.Should().Be(1);
        mapped.Limit.Should().Be(2);
        mapped.OrderBy.Should().Be("name");
        mapped.Direction.Should().Be("desc");
        mapped.IsDescending.Should().BeTrue();
    }
}

/// <summary>
/// Extracts the field names from the shared <see cref="PagingQueryValidator{T}"/> orderBy message
/// ("'orderBy' is not a valid field. Allowed values: a, b."). The list is built from a set, so tests
/// compare membership rather than the rendered order.
/// </summary>
internal static class PagingValidatorMessageAssertions
{
    private const string AllowedValuesMarker = "Allowed values:";

    public static string[] AllowedValuesIn(string message)
    {
        int markerIndex = message.IndexOf(AllowedValuesMarker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, "the shared orderBy message lists the allowed values");

        return message[(markerIndex + AllowedValuesMarker.Length)..]
            .TrimEnd('.')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
