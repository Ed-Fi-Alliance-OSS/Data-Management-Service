// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetProviderOptionsValidator
{
    private readonly MappingSetProviderOptionsValidator _validator = new();

    [TestFixture]
    public class Given_Default_Options : Given_MappingSetProviderOptionsValidator
    {
        [Test]
        public void It_passes_validation()
        {
            var result = _validator.Validate(null, new MappingSetProviderOptions());

            result.Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Required_True_And_Enabled_True : Given_MappingSetProviderOptionsValidator
    {
        [Test]
        public void It_passes_validation()
        {
            var result = _validator.Validate(
                null,
                new MappingSetProviderOptions { Required = true, Enabled = true }
            );

            result.Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Required_True_And_Enabled_False : Given_MappingSetProviderOptionsValidator
    {
        [Test]
        public void It_fails_validation()
        {
            var result = _validator.Validate(
                null,
                new MappingSetProviderOptions { Required = true, Enabled = false }
            );

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("Required is true").And.Contain("Enabled is false");
        }
    }

    [TestFixture]
    public class Given_Invalid_CacheMode : Given_MappingSetProviderOptionsValidator
    {
        [Test]
        public void It_fails_validation()
        {
            var result = _validator.Validate(
                null,
                new MappingSetProviderOptions { CacheMode = (MappingSetCacheMode)999 }
            );

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("CacheMode");
        }
    }

    [TestFixture]
    public class Given_Enabled_With_Fallback_Allowed : Given_MappingSetProviderOptionsValidator
    {
        [Test]
        public void It_passes_validation()
        {
            var result = _validator.Validate(
                null,
                new MappingSetProviderOptions
                {
                    Enabled = true,
                    AllowRuntimeCompileFallback = true,
                    CacheMode = MappingSetCacheMode.InMemory,
                }
            );

            result.Succeeded.Should().BeTrue();
        }
    }
}
