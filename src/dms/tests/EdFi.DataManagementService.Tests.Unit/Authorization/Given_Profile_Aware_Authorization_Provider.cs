// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit.Authorization;

[TestFixture]
public class Given_Profile_Aware_Authorization_Provider_When_Requesting_Client_Credentials_Without_An_Explicit_Claim_Set
{
    private string _defaultClaimSetName = null!;

    [SetUp]
    public void Setup()
    {
        var createClientCredentialsMethod =
            typeof(ProfileAwareAuthorizationProvider).GetMethod(
                nameof(ProfileAwareAuthorizationProvider.CreateClientCredentialsWithProfiles)
            )
            ?? throw new InvalidOperationException(
                "CreateClientCredentialsWithProfiles method was not found."
            );

        _defaultClaimSetName = (string)(
            createClientCredentialsMethod
                .GetParameters()
                .Single(parameter => parameter.Name == "claimSetName")
                .DefaultValue
            ?? throw new InvalidOperationException("claimSetName default value was not found.")
        );
    }

    [Test]
    public void It_keeps_the_existing_no_further_auth_required_claim_set_default()
    {
        _defaultClaimSetName.Should().Be(AuthorizationClaimSetNames.NoFurtherAuthRequired);
    }
}
