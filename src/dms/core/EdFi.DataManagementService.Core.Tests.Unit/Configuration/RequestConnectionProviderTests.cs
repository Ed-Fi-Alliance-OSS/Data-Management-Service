// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
[Parallelizable]
public class Given_DmsInstanceRequestConnectionProvider
{
    private IDmsInstanceSelection _dmsInstanceSelection = null!;
    private IRequestConnectionProvider _requestConnectionProvider = null!;

    [SetUp]
    public void Setup()
    {
        _dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
        _requestConnectionProvider = new DmsInstanceRequestConnectionProvider(_dmsInstanceSelection);
    }

    [Test]
    public void It_returns_the_selected_request_connection()
    {
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance())
            .Returns(
                new DmsInstance(
                    Id: 7,
                    InstanceType: "Test",
                    InstanceName: "Selected Instance",
                    ConnectionString: "Server=localhost;Database=test;",
                    RouteContext: []
                )
            );

        RequestConnection result = _requestConnectionProvider.GetRequestConnection();

        result.DmsInstanceId.Should().Be(new DmsInstanceId(7));
        result.ConnectionString.Should().Be("Server=localhost;Database=test;");
    }

    [Test]
    public void It_throws_when_the_selected_instance_has_not_been_set()
    {
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance())
            .Throws(
                new InvalidOperationException(
                    "DMS instance has not been set for this request. "
                        + "Ensure ResolveDmsInstanceMiddleware is registered in the pipeline before repositories are accessed."
                )
            );

        var act = () => _requestConnectionProvider.GetRequestConnection();

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_throws_when_the_selected_instance_has_no_connection_string()
    {
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance())
            .Returns(
                new DmsInstance(
                    Id: 7,
                    InstanceType: "Test",
                    InstanceName: "Selected Instance",
                    ConnectionString: "   ",
                    RouteContext: []
                )
            );

        var act = () => _requestConnectionProvider.GetRequestConnection();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Selected DMS instance '7' does not have a valid connection string.");
    }
}

[TestFixture]
[Parallelizable]
public class Given_DmsCoreServiceExtensions_DefaultConfiguration
{
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddOptions();
        services.AddDmsDefaultConfiguration(
            new LoggerConfiguration().CreateLogger(),
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            false
        );

        _serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
    }

    [Test]
    public void It_registers_the_request_connection_provider_adapter()
    {
        using var scope = _serviceProvider.CreateScope();

        var dmsInstanceSelection = scope.ServiceProvider.GetRequiredService<IDmsInstanceSelection>();
        var requestConnectionProvider =
            scope.ServiceProvider.GetRequiredService<IRequestConnectionProvider>();

        dmsInstanceSelection.SetSelectedDmsInstance(
            new DmsInstance(
                Id: 11,
                InstanceType: "Test",
                InstanceName: "Configured Instance",
                ConnectionString: "Server=localhost;Database=configured;",
                RouteContext: []
            )
        );

        requestConnectionProvider.Should().BeOfType<DmsInstanceRequestConnectionProvider>();
        requestConnectionProvider
            .GetRequestConnection()
            .Should()
            .Be(new RequestConnection(new DmsInstanceId(11), "Server=localhost;Database=configured;"));
    }
}
