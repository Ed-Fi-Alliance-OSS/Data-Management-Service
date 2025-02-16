// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class NamedAuthorizationServiceFactoryTests
{
    [TestFixture]
    public class Given_Matching_AuthorizationStrategy_Handler
    {
        private NamedAuthorizationServiceFactory? handlerProvider;
        private ServiceProvider? serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<NamedAuthorizationServiceFactory>();
            services.AddTransient<NoFurtherAuthorizationRequiredValidator>();
            services.AddTransient<NamespaceBasedValidator>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationServiceFactory(serviceProvider);
        }

        [Test]
        public void Should_Return_NoFurtherAuthorizationRequiredValidator()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationValidator>("NoFurtherAuthorizationRequired")
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
            var handler =
                handlerProvider!.GetByName<IAuthorizationValidator>("NamespaceBased")
                as NamespaceBasedValidator;
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
    public class Given_Not_Matching_AuthorizationStrategy_Handler
    {
        private NamedAuthorizationServiceFactory? handlerProvider;
        private ServiceProvider? serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<NamedAuthorizationServiceFactory>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationServiceFactory(serviceProvider);
        }

        [Test]
        public void Should_Return_Null()
        {
            var handler = handlerProvider!.GetByName<IAuthorizationValidator>("NotMatchingHandler");
            handler.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Matching_AuthorizationFilters_Handler
    {
        private NamedAuthorizationServiceFactory? handlerProvider;
        private ServiceProvider? serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<NamedAuthorizationServiceFactory>();
            services.AddTransient<NoFurtherAuthorizationRequiredFiltersProvider>();
            services.AddTransient<NamespaceBasedFiltersProvider>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationServiceFactory(serviceProvider);
        }

        [Test]
        public void Should_Return_NoFurtherAuthorizationRequiredValidator()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationFiltersProvider>("NoFurtherAuthorizationRequired")
                as NoFurtherAuthorizationRequiredFiltersProvider;
            handler.Should().NotBeNull();
            var filters = handler!.GetFilters(
                new ApiClientDetails("", "", [], [])
            );
            filters.Should().NotBeNull();
            filters.Filters.Should().BeEmpty();
        }

        [Test]
        public void Should_Return_NamespaceBasedValidator()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationFiltersProvider>("NamespaceBased")
                as NamespaceBasedFiltersProvider;
            handler.Should().NotBeNull();
            var filters = handler!.GetFilters(
                new ApiClientDetails("", "", [], [new NamespacePrefix("uri://namespace")])
            );
            filters.Should().NotBeNull();
            filters.Filters.Should().NotBeEmpty();
            filters.Filters[0].FilterPath.Should().Be("Namespace");
            filters.Filters[0].Value.Should().Be("uri://namespace");
        }
    }

    [TestFixture]
    public class Given_Not_Matching_AuthorizationFilters_Handler
    {
        private NamedAuthorizationServiceFactory? handlerProvider;
        private ServiceProvider? serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<NamedAuthorizationServiceFactory>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationServiceFactory(serviceProvider);
        }

        [Test]
        public void Should_Return_Null_Authorization_Validator()
        {
            var handler = handlerProvider!.GetByName<IAuthorizationValidator>("NotMatchingHandler");
            handler.Should().BeNull();
        }

        [Test]
        public void Should_Return_Null_Authorization_Filters()
        {
            var handler = handlerProvider!.GetByName<IAuthorizationFiltersProvider>("NotMatchingHandler");
            handler.Should().BeNull();
        }
    }
}
