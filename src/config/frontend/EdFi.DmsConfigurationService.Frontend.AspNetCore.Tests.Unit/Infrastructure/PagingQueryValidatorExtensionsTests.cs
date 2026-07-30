// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentAssertions;
using FluentValidation;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Proves the overload-resolution design: a concrete <see cref="PagingQueryValidator{T}"/> selects the
/// more specific <c>GuardAsync</c> overload and throws <see cref="ParameterValidationException"/> (an
/// intentional behavior change for every paging/query call site), while an ordinary (non-paging)
/// <see cref="AbstractValidator{T}"/> still resolves to the original generic overload and throws the plain
/// FluentValidation <see cref="ValidationException"/> — exactly as it did before this change, since no
/// existing non-paging call site's behavior is altered.
/// </summary>
public class PagingQueryValidatorExtensionsTests
{
    private sealed record SimpleValidatedRequest(string? Name = null);

    private sealed class SimpleRequestValidator : AbstractValidator<SimpleValidatedRequest>
    {
        public SimpleRequestValidator()
        {
            RuleFor(r => r.Name).NotEmpty();
        }
    }

    [TestFixture]
    public class Given_a_concrete_paging_query_validator_guards_an_invalid_query
    {
        private VendorPagingQueryValidator _validator = null!;
        private FrontendVendorQuery _query = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new VendorPagingQueryValidator();
            _query = new FrontendVendorQuery { Limit = 0 };
        }

        [Test]
        public async Task It_throws_a_parameter_validation_exception()
        {
            Func<Task> act = () => _validator.GuardAsync(_query);
            await act.Should().ThrowAsync<ParameterValidationException>();
        }
    }

    [TestFixture]
    public class Given_an_ordinary_validator_guards_an_invalid_request
    {
        private SimpleRequestValidator _validator = null!;
        private SimpleValidatedRequest _request = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new SimpleRequestValidator();
            _request = new SimpleValidatedRequest();
        }

        [Test]
        public async Task It_still_throws_the_plain_fluent_validation_exception()
        {
            Func<Task> act = () => _validator.GuardAsync(_request);
            var assertion = await act.Should().ThrowAsync<ValidationException>();
            assertion.Which.Should().NotBeOfType<ParameterValidationException>();
        }
    }
}
