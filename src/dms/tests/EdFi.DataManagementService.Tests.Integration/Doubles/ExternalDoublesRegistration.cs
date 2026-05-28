// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Replaces every DI binding the API integration harness needs to fake (auth/CMS/
/// application-context/profile-catalog/DMS-instance) and leaves the rest of the
/// DMS HTTP pipeline real. Called from the test base's WebApplicationFactory
/// ConfigureServices override.
/// </summary>
internal static class ExternalDoublesRegistration
{
    public static void RegisterAll(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString,
        IClaimSetProvider claimSetProvider,
        IReadOnlyList<long> clientEducationOrganizationIds
    )
    {
        services.RemoveAll<IJwtValidationService>();
        services.RemoveAll<IConfigurationManager<OpenIdConnectConfiguration>>();
        services.RemoveAll<IClaimSetProvider>();
        services.RemoveAll<IApplicationContextProvider>();
        services.RemoveAll<IDmsInstanceProvider>();
        services.RemoveAll<IProfileCmsProvider>();
        services.RemoveAll<IStartupProcessExit>();

        services.AddSingleton<IJwtValidationService>(
            FakeJwtValidationService.Allowing(
                ExternalDoublesConstants.SmokeToken,
                ExternalDoublesConstants.SmokeClientId,
                clientEducationOrganizationIds
            )
        );
        services.AddSingleton(FakeOidcConfigurationManager.Stable());
        services.AddSingleton(claimSetProvider);
        services.AddSingleton<IApplicationContextProvider>(FakeApplicationContextProvider.Stable());
        services.AddSingleton<IDmsInstanceProvider>(
            FakeDmsInstanceProvider.WithSingleInstance(
                id: ExternalDoublesConstants.StableDmsInstanceId,
                connectionString: leasedConnectionString
            )
        );
        services.AddSingleton<IProfileCmsProvider>(FakeProfileCmsProvider.FromFixture(fixture));
        services.AddSingleton<IStartupProcessExit, NonExitingStartupProcessExit>();
    }
}
