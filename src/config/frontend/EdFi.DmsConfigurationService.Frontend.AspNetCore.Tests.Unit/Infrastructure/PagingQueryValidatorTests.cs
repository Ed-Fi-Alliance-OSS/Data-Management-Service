// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
public class Given_PagingQueryValidators
{
    private interface IValidationScenario
    {
        ValidationResult Validate();

        bool ShouldBeValid { get; }
    }

    private sealed record ValidationScenario<T>(AbstractValidator<T> Validator, T Query, bool ShouldBeValid)
        : IValidationScenario
        where T : PagingQuery
    {
        public ValidationResult Validate()
        {
            return Validator.Validate(Query);
        }
    }

    [Test]
    public void Limit_accepts_large_values_for_vendor()
    {
        var scenario = new ValidationScenario<FrontendVendorQuery>(
            new VendorPagingQueryValidator(),
            new FrontendVendorQuery { Limit = 1000 },
            true
        );
        var result = scenario.Validate();
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Limit_accepts_large_values_for_application()
    {
        var scenario = new ValidationScenario<FrontendApplicationQuery>(
            new ApplicationPagingQueryValidator(),
            new FrontendApplicationQuery { Limit = 1000 },
            true
        );
        var result = scenario.Validate();
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Limit_accepts_large_values_for_api_client()
    {
        var scenario = new ValidationScenario<FrontendApiClientQuery>(
            new ApiClientPagingQueryValidator(),
            new FrontendApiClientQuery { Limit = 1000 },
            true
        );
        var result = scenario.Validate();
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Limit_accepts_large_values_for_data_store()
    {
        var scenario = new ValidationScenario<FrontendDataStoreQuery>(
            new DataStorePagingQueryValidator(),
            new FrontendDataStoreQuery { Limit = 1000 },
            true
        );
        var result = scenario.Validate();
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void Limit_accepts_large_values_for_claim_set()
    {
        var scenario = new ValidationScenario<FrontendClaimSetQuery>(
            new ClaimSetPagingQueryValidator(),
            new FrontendClaimSetQuery { Limit = 1000 },
            true
        );
        var result = scenario.Validate();
        result.IsValid.Should().BeTrue();
    }
}
