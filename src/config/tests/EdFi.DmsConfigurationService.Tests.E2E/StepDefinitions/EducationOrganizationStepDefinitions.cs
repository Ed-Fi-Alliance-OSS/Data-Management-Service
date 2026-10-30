// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Keycloak;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

/// <summary>
/// Reads an API client's education organizations as the identity provider issued them in a real
/// token, and as the Configuration Service reports them.
/// </summary>
public partial class StepDefinitions
{
    private const string EducationOrganizationIdsClaim = "educationOrganizationIds";

    private async Task<string> CurrentAccessTokenAsync()
    {
        JsonNode responseJson = JsonNode.Parse(await _apiResponse.TextAsync())!;
        responseJson["access_token"].Should().NotBeNull("response should include an access_token");
        return responseJson["access_token"]!.GetValue<string>();
    }

    /// <summary>
    /// The token holds exactly one educationOrganizationIds claim whose value is exactly the given
    /// comma-separated ids, in the ascending order the Configuration Service sends them.
    /// </summary>
    [Then("the token carries exactly the education organizations {string}")]
    public async Task ThenTheTokenCarriesExactlyTheEducationOrganizations(string educationOrganizationIds)
    {
        JwtTokenValidator
            .TryGetClaimValues(
                await CurrentAccessTokenAsync(),
                EducationOrganizationIdsClaim,
                out IReadOnlyList<string> values
            )
            .Should()
            .BeTrue("the token should carry an educationOrganizationIds claim");
        values.Should().Equal([educationOrganizationIds], "the token should carry exactly the client's list");
    }

    /// <summary>
    /// The token states an empty scope the way its identity provider does, each exactly: the
    /// self-contained provider issues one present, empty educationOrganizationIds claim, and Keycloak's
    /// hardcoded-claim mapper issues no claim for an empty value. DMS reads both as no education
    /// organization access, since it treats a missing claim as an empty one.
    /// </summary>
    [Then("the token carries no education organizations")]
    public async Task ThenTheTokenCarriesNoEducationOrganizations()
    {
        bool present = JwtTokenValidator.TryGetClaimValues(
            await CurrentAccessTokenAsync(),
            EducationOrganizationIdsClaim,
            out IReadOnlyList<string> values
        );

        if (KeycloakCharacterizationEnvironment.IsKeycloakProvider)
        {
            present.Should().BeFalse("Keycloak issues no educationOrganizationIds claim for an empty scope");
        }
        else
        {
            present
                .Should()
                .BeTrue("the self-contained provider issues an empty educationOrganizationIds claim");
            values.Should().Equal([""], "an empty scope is one empty claim");
        }
    }

    /// <summary>
    /// The API client in the current response reports this inheritance flag and exactly these
    /// effective education organizations, comma-separated in ascending order ("" for none).
    /// </summary>
    [Then("the response body has inheritance {string} and education organizations {string}")]
    public async Task ThenTheResponseBodyHasInheritanceAndEducationOrganizations(
        string inherits,
        string educationOrganizationIds
    )
    {
        JsonNode responseJson = JsonNode.Parse(await _apiResponse.TextAsync())!;

        responseJson["inheritsApplicationEducationOrganizations"]!
            .GetValue<bool>()
            .Should()
            .Be(bool.Parse(inherits));
        string.Join(
                ",",
                responseJson["educationOrganizationIds"]!.AsArray().Select(id => id!.GetValue<long>())
            )
            .Should()
            .Be(educationOrganizationIds);
    }
}
