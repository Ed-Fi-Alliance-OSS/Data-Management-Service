// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// The approval of the API clients registered under an application's client id, aggregated over
/// every matching <c>dmscs.ApiClient</c> row. The numeric values are a contract: both engines
/// compute them in SQL, and the zero value refuses, so a value nothing assigned is never read as an
/// approval. A value that is not one of the named members is not admissible anywhere.
/// </summary>
public enum ApiClientApprovalState
{
    /// <summary>At least one API client row exists and at least one of them is not approved.</summary>
    Unapproved = 0,

    /// <summary>At least one API client row exists and every one of them is approved.</summary>
    Approved = 1,

    /// <summary>
    /// No API client row exists: the identity provider knows the client and the Management API does
    /// not. This is neither an approval nor an un-approval.
    /// </summary>
    NoApiClientRow = 2,
}
