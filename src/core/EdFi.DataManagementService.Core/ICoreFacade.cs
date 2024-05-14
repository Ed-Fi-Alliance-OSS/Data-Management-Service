// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// The facade a frontend uses to access DMS Core.
///
/// The intent of this design is to provide a web framework-independent interface
/// for 1) ease of testing and 2) ease of supporting future frontends e.g.
/// AWS Lambda, Azure functions, etc.
/// </summary>
public interface ICoreFacade
{
    /// <summary>
    /// DMS entry point for API upsert requests
    /// </summary>
    public Task<FrontendResponse> Upsert(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for all API GET by id requests
    /// </summary>
    public Task<FrontendResponse> GetById(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for all API PUT requests, which are "by id"
    /// </summary>
    public Task<FrontendResponse> UpdateById(FrontendRequest frontendRequest);

    /// <summary>
    /// DMS entry point for all API DELETE requests, which are "by id"
    /// </summary>
    public Task<FrontendResponse> DeleteById(FrontendRequest frontendRequest);
}
