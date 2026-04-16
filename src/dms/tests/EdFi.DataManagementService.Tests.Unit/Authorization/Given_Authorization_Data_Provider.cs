// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit.Authorization;

[TestFixture]
public class Given_Authorization_Data_Provider_When_Requesting_Client_Credentials_Without_An_Explicit_Claim_Set
{
    private string _defaultClaimSetName = null!;

    [SetUp]
    public void Setup()
    {
        var createClientCredentialsMethod =
            typeof(AuthorizationDataProvider).GetMethod(
                nameof(AuthorizationDataProvider.CreateClientCredentials)
            ) ?? throw new InvalidOperationException("CreateClientCredentials method was not found.");

        _defaultClaimSetName = (string)(
            createClientCredentialsMethod
                .GetParameters()
                .Single(parameter => parameter.Name == "claimSetName")
                .DefaultValue
            ?? throw new InvalidOperationException("claimSetName default value was not found.")
        );
    }

    [Test]
    public void It_defaults_to_the_sis_vendor_claim_set()
    {
        _defaultClaimSetName.Should().Be(AuthorizationClaimSetNames.SisVendor);
    }
}

[TestFixture]
public class Given_Authorization_Data_Provider_When_Building_An_Application_Request
{
    private JsonDocument _requestDocument = null!;
    private string _claimSetName = null!;

    [SetUp]
    public void Setup()
    {
        _claimSetName = "CustomClaimSet";

        string requestJson = AuthorizationDataProvider.CreateApplicationRequestJson(
            vendorId: 1,
            claimSetName: _claimSetName,
            educationOrganizationIds: [255901],
            dmsInstanceId: 2
        );

        _requestDocument = JsonDocument.Parse(requestJson);
    }

    [TearDown]
    public void TearDown()
    {
        _requestDocument.Dispose();
    }

    [Test]
    public void It_preserves_the_explicit_claim_set_name()
    {
        _requestDocument.RootElement.GetProperty("claimSetName").GetString().Should().Be(_claimSetName);
    }
}
