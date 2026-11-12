// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The single place that names the OpenAPI tag of each endpoint module, in the Ed-Fi Management API
/// style. MetadataModule maps a RequestDelegate that is not in the document, so it has no entry.
/// </summary>
public static class OpenApiModuleTags
{
    public static IReadOnlyDictionary<Type, string> ByModule { get; } =
        new Dictionary<Type, string>
        {
            [typeof(ActionsModule)] = "Actions",
            [typeof(ApiClientModule)] = "ApiClients",
            [typeof(ApplicationModule)] = "Applications",
            [typeof(AuthorizationMetadataModule)] = "AuthorizationMetadata",
            [typeof(AuthorizationStrategiesModule)] = "AuthorizationStrategies",
            [typeof(ClaimSetModule)] = "ClaimSets",
            [typeof(ClaimsManagementModule)] = "ClaimsManagement",
            [typeof(DataStoreContextModule)] = "DataStoreContexts",
            [typeof(DataStoreDerivativeModule)] = "DataStoreDerivatives",
            [typeof(DataStoreModule)] = "DataStores",
            [typeof(HealthModule)] = "Health",
            [typeof(IdentityModule)] = "Connect",
            [typeof(InformationModule)] = "Information",
            [typeof(JobModule)] = "Jobs",
            [typeof(JwksEndpointModule)] = "Discovery",
            [typeof(OpenIdConfigurationModule)] = "Discovery",
            [typeof(OpenIddictDiscoveryModule)] = "Discovery",
            [typeof(OwnershipTokenModule)] = "OwnershipTokens",
            [typeof(ProfileModule)] = "Profiles",
            [typeof(ResourceClaimModule)] = "ResourceClaims",
            [typeof(TenantModule)] = "Tenants",
            [typeof(VendorModule)] = "Vendors",
        };
}
