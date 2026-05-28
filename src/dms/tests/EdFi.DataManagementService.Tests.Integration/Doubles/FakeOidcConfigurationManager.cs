// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

internal static class FakeOidcConfigurationManager
{
    public static IConfigurationManager<OpenIdConnectConfiguration> Stable()
    {
        var fake = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
        OpenIdConnectConfiguration configuration = new() { Issuer = "test-idp" };

        A.CallTo(() => fake.GetConfigurationAsync(A<CancellationToken>._))
            .Returns(Task.FromResult(configuration));

        return fake;
    }
}
