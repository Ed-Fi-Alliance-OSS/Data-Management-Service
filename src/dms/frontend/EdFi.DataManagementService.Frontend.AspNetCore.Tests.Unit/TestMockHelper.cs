// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>.Ignored)).Returns([]);
        services.AddTransient(x => claimSetProvider);

        // Mock IDataStoreProvider
        var dataStoreProvider = A.Fake<IDataStoreProvider>();
        var mockInstance = new DataStore(1, "Test", "TestInstance", "test-connection-string", []);
        A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>.Ignored)).Returns([mockInstance]);
        A.CallTo(() => dataStoreProvider.LoadTenants()).Returns(new List<string> { "TestTenant" });
        A.CallTo(() => dataStoreProvider.GetAll(A<string?>.Ignored)).Returns([mockInstance]);
        A.CallTo(() => dataStoreProvider.GetById(A<long>.Ignored, A<string?>.Ignored)).Returns(mockInstance);
        A.CallTo(() => dataStoreProvider.IsLoaded(A<string?>.Ignored)).Returns(true);
        A.CallTo(() => dataStoreProvider.TenantExists(A<string>.That.IsNotNull())).Returns(true);
        A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys()).Returns(new List<string> { "" }.AsReadOnly());
        services.AddTransient(x => dataStoreProvider);

        // Mock ITenantValidator
        var tenantValidator = A.Fake<ITenantValidator>();
        A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>.That.IsNotNull())).Returns(true);
        services.AddTransient(x => tenantValidator);

        // Mock IConnectionStringProvider
        var connectionStringProvider = A.Fake<IConnectionStringProvider>();
        A.CallTo(() => connectionStringProvider.GetConnectionString(A<long>._, A<string?>.Ignored))
            .Returns("test-connection-string");
        A.CallTo(() => connectionStringProvider.GetHealthCheckConnectionString())
            .Returns("test-connection-string");
        services.AddTransient(x => connectionStringProvider);

        // Replace the runtime backend mapping initializer so full app startup
        // does not terminate the test host when backend mapping compilation fails.
        var backendMappingInitializer = A.Fake<IBackendMappingInitializer>();
        A.CallTo(() => backendMappingInitializer.InitializeAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        services.Replace(ServiceDescriptor.Singleton(backendMappingInitializer));
    }
}
