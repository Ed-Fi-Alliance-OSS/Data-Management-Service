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
public class Given_A_Valid_Paging_Query
{
    private Func<Task> _act = null!;

    [SetUp]
    public void Setup()
    {
        var validator = new VendorPagingQueryValidator();
        var query = new FrontendVendorQuery
        {
            Offset = 0,
            Limit = 10,
            OrderBy = "id",
            Direction = "asc",
        };
        _act = () => validator.GuardQueryAsync(query);
    }

    [Test]
    public async Task It_completes_without_throwing()
    {
        await _act.Should().NotThrowAsync();
    }
}

[TestFixture]
public class Given_A_Paging_Query_With_Multiple_Invalid_Parameters
{
    private const string MaliciousOrderBy = "malicious'; DROP TABLE Vendors;--";

    // Sorted ordinal-ignore-case allowed set for the vendor validator (id, company, contactName,
    // contactEmailAddress) — value-free and deterministic.
    private const string ExpectedOrderByMessage =
        "'orderBy' is not a valid field. Allowed values: company, contactEmailAddress, contactName, id.";

    private ParameterValidationException _exception = null!;

    [SetUp]
    public async Task Setup()
    {
        var validator = new VendorPagingQueryValidator();
        var query = new FrontendVendorQuery
        {
            Offset = -1,
            Limit = 0,
            Direction = "sideways",
            OrderBy = MaliciousOrderBy,
        };

        _exception = (
            await validator
                .Invoking(v => v.GuardQueryAsync(query))
                .Should()
                .ThrowAsync<ParameterValidationException>()
        ).Which;
    }

    [Test]
    public void It_reports_all_failures_in_validator_declaration_order()
    {
        _exception
            .Errors.Should()
            .Equal(
                "'offset' must be greater than or equal to 0.",
                "'limit' must be greater than 0.",
                "The direction query parameter must be one of: asc, ascending, desc, descending.",
                ExpectedOrderByMessage
            );
    }

    [Test]
    public void It_does_not_echo_the_supplied_orderBy_value()
    {
        string joined = string.Join(" ", _exception.Errors);
        joined.Should().NotContain("malicious");
        joined.Should().NotContain("DROP");
    }

    [Test]
    public void It_lists_the_allowed_orderBy_fields_sorted_ordinal_ignore_case()
    {
        _exception.Errors.Should().Contain(ExpectedOrderByMessage);
    }
}
