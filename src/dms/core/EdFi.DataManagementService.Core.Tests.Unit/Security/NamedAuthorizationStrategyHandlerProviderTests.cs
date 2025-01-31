// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Net;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationStrategies;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class NamedAuthorizationStrategyHandlerProviderTests
{
    [TestFixture]
    public class Given_Service_Receives_Expected_Data : SecurityMetadataServiceTests
    {
        private IAuthorizationStrategyHandlerProvider? _handlerProvider;
        private ServiceProvider? _serviceProvider;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddTransient<ITestDependency, TestDependency>();
            services.AddTransient<IAuthorizationStrategyHandler, TestAuthorizationStrategyOneHandler>();
            services.AddTransient<IAuthorizationStrategyHandler, TestAuthorizationStrategyTwoHandler>();
            services.AddSingleton<
                IAuthorizationStrategyHandlerProvider,
                NamedAuthorizationStrategyHandlerProvider
            >();

            _serviceProvider = services.BuildServiceProvider();

            _handlerProvider = new NamedAuthorizationStrategyHandlerProvider(_serviceProvider);
        }

        [Test]
        public void Should_Return_From_TestAuthorizationStrategyOneHandler()
        {
            var handlerOne =
                _handlerProvider!.GetByName("TestAuthorizationStrategyOne")
                as TestAuthorizationStrategyOneHandler;
            handlerOne.Should().NotBeNull();
            var authResult = handlerOne!.IsRequestAuthorized(
                new DocumentSecurityElements([]),
                new ApiClientDetails("", "", [], [])
            );
            authResult.Should().NotBeNull();
            authResult.IsAuthorized.Should().BeFalse();
            authResult.ErrorMessage.Should().Be("TestAuthorizationStrategyOne");
        }
    }

    [AuthorizationStrategyName(AuthorizationStrategyName)]
    public class TestAuthorizationStrategyOneHandler(ITestDependency testDependency)
        : IAuthorizationStrategyHandler
    {
        private const string AuthorizationStrategyName = "TestAuthorizationStrategyOne";

        public AuthorizationResult IsRequestAuthorized(
            DocumentSecurityElements securityElements,
            ApiClientDetails details
        )
        {
            testDependency.TestMethod(AuthorizationStrategyName);
            return new AuthorizationResult(false, AuthorizationStrategyName);
        }
    }

    [AuthorizationStrategyName(AuthorizationStrategyName)]
    public class TestAuthorizationStrategyTwoHandler : IAuthorizationStrategyHandler
    {
        private const string AuthorizationStrategyName = "TestAuthorizationStrategyTwo";

        public AuthorizationResult IsRequestAuthorized(
            DocumentSecurityElements securityElements,
            ApiClientDetails details
        )
        {
            return new AuthorizationResult(false, $"error-from-{AuthorizationStrategyName}");
        }
    }

    public interface ITestDependency
    {
        string TestMethod(string name);
    }

    public class TestDependency : ITestDependency
    {
        public string TestMethod(string name)
        {
            return name;
        }
    }
}
