// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        string leasedConnectionString
    )
    {
        services.RemoveAll<IJwtValidationService>();
        services.RemoveAll<IClaimSetProvider>();
        services.RemoveAll<IApplicationContextProvider>();
        services.RemoveAll<IDmsInstanceProvider>();
        services.RemoveAll<IProfileCmsProvider>();

        services.AddSingleton<IJwtValidationService>(
            FakeJwtValidationService.Allowing(
                ExternalDoublesConstants.SmokeToken,
                ExternalDoublesConstants.SmokeClientId
            )
        );
        services.AddSingleton<IClaimSetProvider>(new AllowAllClaimSetProvider(fixture));
        services.AddSingleton<IApplicationContextProvider>(FakeApplicationContextProvider.Stable());
        services.AddSingleton<IDmsInstanceProvider>(
            FakeDmsInstanceProvider.WithSingleInstance(
                id: ExternalDoublesConstants.StableDmsInstanceId,
                connectionString: leasedConnectionString
            )
        );
        services.AddSingleton<IProfileCmsProvider>(FakeProfileCmsProvider.FromFixture(fixture));
    }
}
