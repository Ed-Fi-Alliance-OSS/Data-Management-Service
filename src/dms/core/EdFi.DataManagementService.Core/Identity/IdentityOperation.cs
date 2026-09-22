// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// The five fixed identity operations DMS exposes under <c>/identity/v2</c>. Set on
/// <see cref="Pipeline.RequestInfo.IdentityOperation" /> by the facade before the identity pipeline
/// runs, so downstream steps such as <see cref="Middleware.ServiceClaimAuthorizationMiddleware" /> know
/// which CMS action (<c>Create</c> or <c>Read</c>) the request requires.
/// </summary>
internal enum IdentityOperation
{
    /// <summary>POST /identity/v2/identities</summary>
    Create,

    /// <summary>GET /identity/v2/identities/{id}</summary>
    GetById,

    /// <summary>POST /identity/v2/identities/find</summary>
    Find,

    /// <summary>POST /identity/v2/identities/search</summary>
    Search,

    /// <summary>GET /identity/v2/identities/results/{token}</summary>
    Results,
}
