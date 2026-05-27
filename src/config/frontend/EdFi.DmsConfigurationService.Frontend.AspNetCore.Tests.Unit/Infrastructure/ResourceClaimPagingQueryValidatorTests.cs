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
public class ResourceClaimPagingQueryValidatorTests
{
    private readonly ResourceClaimPagingQueryValidator _validator = new();

    [Test]
    public void It_allows_valid_orderBy_id()
    {
        var query = new FrontendResourceClaimQuery { OrderBy = "id" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_name()
    {
        var query = new FrontendResourceClaimQuery { OrderBy = "name" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_parentId()
    {
        var query = new FrontendResourceClaimQuery { OrderBy = "parentId" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_parentName()
    {
        var query = new FrontendResourceClaimQuery { OrderBy = "parentName" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_is_case_insensitive_for_orderBy()
    {
        var query = new FrontendResourceClaimQuery { OrderBy = "NAME" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_invalid_orderBy()
    {
        var query = new FrontendResourceClaimQuery { OrderBy = "invalidField" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("orderBy");
    }

    [Test]
    public void It_allows_valid_direction_asc()
    {
        var query = new FrontendResourceClaimQuery { Direction = "asc" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_direction_desc()
    {
        var query = new FrontendResourceClaimQuery { Direction = "desc" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_invalid_direction()
    {
        var query = new FrontendResourceClaimQuery { Direction = "sideways" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("direction");
    }

    [Test]
    public void It_rejects_negative_offset()
    {
        var query = new FrontendResourceClaimQuery { Offset = -1 };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("offset");
    }

    [Test]
    public void It_rejects_negative_limit()
    {
        var query = new FrontendResourceClaimQuery { Limit = -1 };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("limit");
    }

    [Test]
    public void It_allows_null_optional_parameters()
    {
        var query = new FrontendResourceClaimQuery
        {
            OrderBy = null,
            Direction = null,
            Offset = null,
            Limit = null,
        };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class ResourceClaimActionPagingQueryValidatorTests
{
    private readonly ResourceClaimActionPagingQueryValidator _validator = new();

    [Test]
    public void It_allows_valid_orderBy_resourceClaimId()
    {
        var query = new FrontendResourceClaimActionQuery { OrderBy = "resourceClaimId" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_resourceName()
    {
        var query = new FrontendResourceClaimActionQuery { OrderBy = "resourceName" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_is_case_insensitive()
    {
        var query = new FrontendResourceClaimActionQuery { OrderBy = "RESOURCENAME" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_invalid_orderBy()
    {
        var query = new FrontendResourceClaimActionQuery { OrderBy = "claimName" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("orderBy");
    }

    [Test]
    public void It_allows_null_optional_parameters()
    {
        var query = new FrontendResourceClaimActionQuery
        {
            OrderBy = null,
            Direction = null,
            Offset = null,
            Limit = null,
            ResourceName = null,
        };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class ResourceClaimActionAuthStrategyPagingQueryValidatorTests
{
    private readonly ResourceClaimActionAuthStrategyPagingQueryValidator _validator = new();

    [Test]
    public void It_allows_valid_orderBy_resourceClaimId()
    {
        var query = new FrontendResourceClaimActionAuthStrategyQuery { OrderBy = "resourceClaimId" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_resourceName()
    {
        var query = new FrontendResourceClaimActionAuthStrategyQuery { OrderBy = "resourceName" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_allows_valid_orderBy_claimName()
    {
        var query = new FrontendResourceClaimActionAuthStrategyQuery { OrderBy = "claimName" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_is_case_insensitive()
    {
        var query = new FrontendResourceClaimActionAuthStrategyQuery { OrderBy = "CLAIMNAME" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_rejects_invalid_orderBy()
    {
        var query = new FrontendResourceClaimActionAuthStrategyQuery { OrderBy = "invalidField" };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("orderBy");
    }

    [Test]
    public void It_allows_null_optional_parameters()
    {
        var query = new FrontendResourceClaimActionAuthStrategyQuery
        {
            OrderBy = null,
            Direction = null,
            Offset = null,
            Limit = null,
            ResourceName = null,
        };
        var result = _validator.Validate(query);
        result.IsValid.Should().BeTrue();
    }
}
