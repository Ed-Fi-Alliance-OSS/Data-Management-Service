// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

/// <summary>
/// The education organization scope an API client write stores, in the same transaction as the
/// rest of the write.
/// </summary>
public abstract record ApiClientEducationOrganizationScope
{
    private ApiClientEducationOrganizationScope() { }

    /// <summary>
    /// The client inherits its Application's current education organizations.
    /// </summary>
    public sealed record Inherit : ApiClientEducationOrganizationScope;

    /// <summary>
    /// The client is narrowed to these education organizations. Every id must be on the client's
    /// Application, or nothing is written.
    /// </summary>
    public sealed record Narrow(long[] EducationOrganizationIds) : ApiClientEducationOrganizationScope;
}
