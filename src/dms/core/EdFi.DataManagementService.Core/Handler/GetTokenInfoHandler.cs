// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles token introspection requests by validating the provided token and returning
/// information about the token's authorizations, including education organizations,
/// resources, services, and assigned profiles.
/// </summary>
internal partial class GetTokenInfoHandler(
    ILogger<GetTokenInfoHandler> logger,
    IJwtValidationService jwtValidationService,
    IClaimSetProvider claimSetProvider,
    IAuthorizationRepository authorizationRepository,
    IApplicationContextProvider applicationContextProvider,
    IProfileService profileService
) : IPipelineStep
{
    private const string EdFiOdsServiceClaimBaseUri = $"{Conventions.EdFiOdsServiceClaimBaseUri}/";
    private const string ErrorDetail = "An invalid token was provided";
    private const string NotProcessableOrMissingTokenError =
        "The token was not present, or was not processable.";

    async Task IPipelineStep.Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            $"Entering {nameof(GetTokenInfoHandler)} - {{TraceId}}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        string? tokenFromBody = null;

        if (!string.IsNullOrWhiteSpace(requestInfo.FrontendRequest.Body))
        {
            try
            {
                tokenFromBody = JsonNode
                    .Parse(requestInfo.FrontendRequest.Body)
                    ?.AsObject()
                    ?.FirstOrDefault(kvp => kvp.Key.Equals("Token", StringComparison.OrdinalIgnoreCase))
                    .Value?.GetValue<string?>();
            }
            catch
            {
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        ErrorDetail,
                        traceId: requestInfo.FrontendRequest.TraceId,
                        [],
                        [NotProcessableOrMissingTokenError]
                    ),
                    Headers: []
                );
                return;
            }
        }
        else if (requestInfo.FrontendRequest.Form != null)
        {
            tokenFromBody = new Dictionary<string, string>(
                requestInfo.FrontendRequest.Form,
                StringComparer.OrdinalIgnoreCase
            ).GetValueOrDefault("Token");
        }

        if (tokenFromBody == null)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForBadRequest(
                    ErrorDetail,
                    traceId: requestInfo.FrontendRequest.TraceId,
                    [],
                    [NotProcessableOrMissingTokenError]
                ),
                Headers: []
            );
            return;
        }

        // Validate token and extract client authorizations
        (ClaimsPrincipal? _, ClientAuthorizations? tokenFromBodyAuthorizations) =
            await jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                tokenFromBody,
                CancellationToken.None
            );

        // Validate that Authorization header token matches body token
        if (requestInfo.ClientAuthorizations.TokenId != tokenFromBodyAuthorizations?.TokenId)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForBadRequest(
                    ErrorDetail,
                    traceId: requestInfo.FrontendRequest.TraceId,
                    [],
                    ["The Authorization header token does not match the token in the request body."]
                ),
                Headers: []
            );
            return;
        }

        // Get authorization metadata
        var claimSets = await claimSetProvider.GetAllClaimSets(requestInfo.FrontendRequest.Tenant);
        var clientClaimSet = claimSets.FindClaimSetByName(requestInfo.ClientAuthorizations.ClaimSetName);

        if (clientClaimSet == null)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 401,
                Body: FailureResponse.ForUnauthorized(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    error: "Unauthorized",
                    description: "The client's ClaimSet was not found."
                ),
                Headers: []
            );
            return;
        }

        // Get application context to find ApplicationId
        ApplicationContext? appContext = await applicationContextProvider.GetApplicationByClientIdAsync(
            requestInfo.ClientAuthorizations.ClientId
        );

        if (appContext == null)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 401,
                Body: FailureResponse.ForUnauthorized(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    error: "Unauthorized",
                    description: "Unable to resolve application context for profile enumeration."
                ),
                Headers: []
            );
            return;
        }

        var response = new TokenInfoResponse
        {
            Active = true,
            ClientId = requestInfo.ClientAuthorizations.ClientId,
            NamespacePrefixes = requestInfo.ClientAuthorizations.NamespacePrefixes.Select(namespacePrefix =>
                namespacePrefix.Value
            ),
            EducationOrganizations = await GetAuthorizedEducationOrganizations(
                requestInfo.ClientAuthorizations.EducationOrganizationIds
            ),
            AssignedProfiles = await GetAssignedProfiles(
                appContext.ApplicationId,
                requestInfo.FrontendRequest.Tenant
            ),
            ClaimSet = new TokenInfoClaimSet { Name = requestInfo.ClientAuthorizations.ClaimSetName },
            Resources = await GetAuthorizedResources(
                clientClaimSet,
                requestInfo.ApiSchemaDocuments.GetAllProjectSchemas()
            ),
            Services = GetAuthorizedServices(clientClaimSet),
        };

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 200,
            Body: JsonSerializer.SerializeToNode(response),
            Headers: new() { ["Cache-Control"] = "no-cache" }
        );
    }

    private async Task<IEnumerable<OrderedDictionary<string, object>>> GetAuthorizedEducationOrganizations(
        IReadOnlyCollection<EducationOrganizationId> clientEducationOrganizationIds
    )
    {
        return (
            await authorizationRepository.GetTokenInfoEducationOrganizations(clientEducationOrganizationIds)
        )
            .OrderBy(edOrg => edOrg.EducationOrganizationId)
            .GroupBy(
                edOrg => (edOrg.EducationOrganizationId, edOrg.NameOfInstitution, edOrg.Discriminator),
                edOrg =>
                {
                    string edOrgIdPropertyName = ConvertPascalToSnakeCase($"{edOrg.AncestorDiscriminator}Id");
                    return new
                    {
                        PropertyName = edOrgIdPropertyName,
                        EducationOrganizationId = edOrg.AncestorEducationOrganizationId,
                    };
                }
            )
            .Select(edOrgGroup =>
            {
                var tokenInfoEducationOrganization = new OrderedDictionary<string, object>();

                // Add properties for current claim value
                var (educationOrganizationId, nameOfInstitution, discriminator) = edOrgGroup.Key;
                tokenInfoEducationOrganization["education_organization_id"] = educationOrganizationId;
                tokenInfoEducationOrganization["name_of_institution"] = nameOfInstitution;
                tokenInfoEducationOrganization["type"] = $"edfi.{discriminator}";

                // Add related ancestor EducationOrganizationIds
                foreach (var ancestorEdOrg in edOrgGroup)
                {
                    tokenInfoEducationOrganization[ancestorEdOrg.PropertyName] =
                        ancestorEdOrg.EducationOrganizationId;
                }

                return tokenInfoEducationOrganization;
            });
    }

    private async Task<IEnumerable<TokenInfoResource>> GetAuthorizedResources(
        ClaimSet clientClaimSet,
        ProjectSchema[] allProjectSchemas
    )
    {
        return allProjectSchemas
            .SelectMany(projectSchema =>
                projectSchema
                    .GetAllResourceSchemaNodes()
                    .Select(jn => (projectSchema, resourceSchema: new ResourceSchema(jn)))
            )
            .Where(resourceSchemaNode => !resourceSchemaNode.resourceSchema.IsResourceExtension)
            .Select(resourceSchemaNode =>
            {
                var authorizedActions = clientClaimSet
                    .FindMatchingResourceClaims(
                        resourceSchemaNode.projectSchema.ProjectEndpointName.GetResourceClaimUri(
                            resourceSchemaNode.resourceSchema.ResourceName
                        )
                    )
                    .Select(resourceClaim => resourceClaim.Action)
                    .WhereNotNull()
                    .Distinct();

                return new TokenInfoResource
                {
                    Resource = resourceSchemaNode.projectSchema.ProjectEndpointName.GetEndpointUri(
                        resourceSchemaNode.projectSchema.GetEndpointNameFromResourceName(
                            resourceSchemaNode.resourceSchema.ResourceName
                        )
                    ),
                    Operations = authorizedActions,
                };
            })
            .Where(resource => resource.Operations.Any())
            .OrderBy(resource => resource.Resource);
    }

    private IEnumerable<TokenInfoService> GetAuthorizedServices(ClaimSet clientClaimSet)
    {
        return clientClaimSet
            .ResourceClaims.Where(resourceClaim => !string.IsNullOrWhiteSpace(resourceClaim.Name))
            .GroupBy(resourceClaim => resourceClaim.Name)
            .Where(resourceClaimByName =>
                resourceClaimByName.Key!.StartsWith(
                    EdFiOdsServiceClaimBaseUri,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(resourceClaimByName => new TokenInfoService
            {
                Service = resourceClaimByName.Key![EdFiOdsServiceClaimBaseUri.Length..],
                Operations = resourceClaimByName
                    .Select(resourceClaim => resourceClaim.Action)
                    .WhereNotNull()
                    .Distinct(),
            })
            .Where(service => service.Operations.Any())
            .OrderBy(service => service.Service);
    }

    private async Task<IEnumerable<string>> GetAssignedProfiles(long ApplicationId, string? tenantId)
    {
        var profiles = await profileService.GetOrFetchApplicationProfilesAsync(ApplicationId, tenantId);
        return profiles.AssignedProfileNames.OrderBy(profile => profile);
    }

    /// <summary>
    /// Converts a pascal cased word to snake case.
    /// </summary>
    /// <param name="pascalCasedWord">The pascal cased word.</param>
    /// <returns>If given `localEducationAgencyId` returns `local_education_agency_id`</returns>
    private static string ConvertPascalToSnakeCase(string pascalCasedWord)
    {
        return WhitespaceAndHyphenRegex()
            .Replace(
                LowercaseToUppercaseRegex()
                    .Replace(UppercaseSequenceRegex().Replace(pascalCasedWord, "$1_$2"), "$1_$2"),
                "_"
            )
            .ToLower();
    }

    // Source-generated regex patterns for pascal to snake case conversion
    [GeneratedRegex(@"([A-Z]+)([A-Z][a-z])")]
    private static partial Regex UppercaseSequenceRegex();

    [GeneratedRegex(@"([a-z\d])([A-Z])")]
    private static partial Regex LowercaseToUppercaseRegex();

    [GeneratedRegex(@"[-\s]")]
    private static partial Regex WhitespaceAndHyphenRegex();
}
