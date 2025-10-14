// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Helper class to provide common test mocks for preventing application crashes during testing
/// </summary>
public static class TestMockHelper
{
    /// <summary>
    /// Adds essential service mocks to prevent application startup crashes during testing
    /// </summary>
    /// <param name="services">The service collection to add mocks to</param>
    public static void AddEssentialMocks(IServiceCollection services)
    {
        // Mock IClaimSetProvider
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets()).Returns([]);
        services.AddTransient(x => claimSetProvider);

        // Mock IDmsInstanceProvider
        var dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
        var mockInstance = new DmsInstance(1, "Test", "TestInstance", "test-connection-string", []);
        A.CallTo(() => dmsInstanceProvider.LoadDmsInstances()).Returns([mockInstance]);
        A.CallTo(() => dmsInstanceProvider.GetAll()).Returns([mockInstance]);
        A.CallTo(() => dmsInstanceProvider.IsLoaded).Returns(true);
        services.AddTransient(x => dmsInstanceProvider);

        // Mock IConnectionStringProvider
        var connectionStringProvider = A.Fake<IConnectionStringProvider>();
        A.CallTo(() => connectionStringProvider.GetConnectionString(A<long>._))
            .Returns("test-connection-string");
        A.CallTo(() => connectionStringProvider.GetHealthCheckConnectionString())
            .Returns("test-connection-string");
        services.AddTransient(x => connectionStringProvider);
    }
}
