// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FakeItEasy;
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
            services.AddTransient<RelationshipsWithEdOrgsOnlyValidator>();

            var fakeAuthorizationRepository = A.Fake<IAuthorizationRepository>();
            A.CallTo(() => fakeAuthorizationRepository.GetAncestorEducationOrganizationIds(A<long[]>.Ignored))
                .Returns(Task.FromResult(new long[] { 255901, 255902 }));
            services.AddTransient(_ => fakeAuthorizationRepository);

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
            var authResult = handler!
                .ValidateAuthorization(
                    new DocumentSecurityElements([], [], [], []),
                    [],
                    [],
                    OperationType.Get
                )
                .Result;
            authResult.Should().NotBeNull();
            authResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }

        [Test]
        public void Should_Return_NamespaceBasedValidator()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationValidator>("NamespaceBased")
                as NamespaceBasedValidator;
            handler.Should().NotBeNull();
            var authResult = handler!
                .ValidateAuthorization(
                    new DocumentSecurityElements(["uri://namespace/resource"], [], [], []),
                    [],
                    [],
                    OperationType.Get
                )
                .Result;
            authResult.Should().NotBeNull();
        }

        [Test]
        public void Should_Return_RelationshipsWithEdOrgsOnlyValidator()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationValidator>("RelationshipsWithEdOrgsOnly")
                as RelationshipsWithEdOrgsOnlyValidator;
            handler.Should().NotBeNull();
            var authResult = handler!
                .ValidateAuthorization(
                    new DocumentSecurityElements(
                        [],
                        [
                            new EducationOrganizationSecurityElement(
                                new ResourceName("School"),
                                new EducationOrganizationId(255901)
                            ),
                        ],
                        [],
                        []
                    ),
                    [],
                    [],
                    OperationType.Get
                )
                .Result;
            authResult.Should().NotBeNull();
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
            services.AddTransient<RelationshipsWithEdOrgsOnlyFiltersProvider>();

            serviceProvider = services.BuildServiceProvider();

            handlerProvider = new NamedAuthorizationServiceFactory(serviceProvider);
        }

        [Test]
        public void Should_Return_NoFurtherAuthorizationRequiredFiltersProvider()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationFiltersProvider>("NoFurtherAuthorizationRequired")
                as NoFurtherAuthorizationRequiredFiltersProvider;
            handler.Should().NotBeNull();
            var filters = handler!.GetFilters(new ClientAuthorizations("", "", [], []));
            filters.Should().NotBeNull();
            filters.Filters.Should().BeEmpty();
        }

        [Test]
        public void Should_Return_NamespaceBasedFiltersProvider()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationFiltersProvider>("NamespaceBased")
                as NamespaceBasedFiltersProvider;
            handler.Should().NotBeNull();
            var filters = handler!.GetFilters(
                new ClientAuthorizations("", "", [], [new NamespacePrefix("uri://namespace")])
            );
            filters.Should().NotBeNull();
            filters.Filters.Should().NotBeEmpty();
            filters.Operator.Should().Be(FilterOperator.Or);
            filters.Filters[0].FilterPath.Should().Be("Namespace");
            filters.Filters[0].Value.Should().Be("uri://namespace");
        }

        [Test]
        public void Should_Return_RelationshipsWithEdOrgsOnlyFiltersProvider()
        {
            var handler =
                handlerProvider!.GetByName<IAuthorizationFiltersProvider>("RelationshipsWithEdOrgsOnly")
                as RelationshipsWithEdOrgsOnlyFiltersProvider;
            handler.Should().NotBeNull();
            var filters = handler!.GetFilters(
                new ClientAuthorizations("", "", [new EducationOrganizationId(255901)], [])
            );
            filters.Should().NotBeNull();
            filters.Filters.Should().NotBeEmpty();
            filters.Operator.Should().Be(FilterOperator.Or);
            filters.Filters[0].FilterPath.Should().Be("EducationOrganization");
            filters.Filters[0].Value.Should().Be("255901");
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
