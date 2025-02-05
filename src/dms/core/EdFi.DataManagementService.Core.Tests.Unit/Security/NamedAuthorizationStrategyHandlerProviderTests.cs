// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class NamedAuthorizationStrategyHandlerProviderTests
{
    [TestFixture]
    public class Given_Matching_AuthorizationStrategy_Handler : ClaimSetCacheServiceTests
    {
        private NamedAuthorizationValidatorProvider? handlerProvider;
        private ServiceProvider? serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<NamedAuthorizationValidatorProvider>();
            services.AddTransient<NoFurtherAuthorizationRequiredValidator>();
            services.AddTransient<NamespaceBasedValidator>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationValidatorProvider(serviceProvider);
        }

        [Test]
        public void Should_Return_NoFurtherAuthorizationRequiredValidator()
        {
            var handler =
                handlerProvider!.GetByName("NoFurtherAuthorizationRequired")
                as NoFurtherAuthorizationRequiredValidator;
            handler.Should().NotBeNull();
            var authResult = handler!.ValidateAuthorization(
                new DocumentSecurityElements([]),
                new ApiClientDetails("", "", [], [])
            );
            authResult.Should().NotBeNull();
            authResult.IsAuthorized.Should().BeTrue();
        }

        [Test]
        public void Should_Return_NamespaceBasedValidator()
        {
            var handler = handlerProvider!.GetByName("NamespaceBased") as NamespaceBasedValidator;
            handler.Should().NotBeNull();
            var authResult = handler!.ValidateAuthorization(
                new DocumentSecurityElements(["uri://namespace/resource"]),
                new ApiClientDetails("", "", [], [new NamespacePrefix("uri://namespace")])
            );
            authResult.Should().NotBeNull();
            authResult.IsAuthorized.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Not_Matching_AuthorizationStrategy_Handler : ClaimSetCacheServiceTests
    {
        private NamedAuthorizationValidatorProvider? handlerProvider;
        private ServiceProvider? serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<NamedAuthorizationValidatorProvider>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationValidatorProvider(serviceProvider);
        }

        [Test]
        public void Should_Return_Null()
        {
            var handler = handlerProvider!.GetByName("NotMatchingHandler");
            handler.Should().BeNull();
        }
    }
}
