// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using Microsoft.Extensions.Options;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Maps the education-organization projection endpoint,
/// <c>GET {prefix}/management/education-organizations</c>, gated by
/// AppSettings:EnableEducationOrganizationProjection (default true). The prefix is the fixed-route
/// prefix: <c>{tenant}</c> first in multi-tenant mode, then each configured route qualifier. With the
/// toggle off nothing is mapped and the route falls through to the fallback 404.
/// </summary>
/// <remarks>
/// The prefix-less path is not mapped in multi-tenant mode, and the legacy
/// <c>/management/{tenant}/...</c> form of the claimset management endpoints is not used.
/// </remarks>
public class EducationOrganizationProjectionEndpointModule(
    IOptions<FrontendAppSettings> frontendOptions,
    IOptions<CoreAppSettings> coreOptions
) : IEndpointModule
{
    /// <summary>
    /// The route after the fixed-route prefix. Discovery advertises the same path.
    /// </summary>
    internal const string RouteSuffix = "/management/education-organizations";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        if (!coreOptions.Value.EnableEducationOrganizationProjection)
        {
            return;
        }

        string prefix = FixedRoutePattern.Build(
            frontendOptions.Value.GetRouteQualifierSegmentsArray(),
            frontendOptions.Value.MultiTenancy
        );

        endpoints.MapGet($"{prefix}{RouteSuffix}", AspNetCoreFrontend.GetEducationOrganizationProjection);
    }
}
