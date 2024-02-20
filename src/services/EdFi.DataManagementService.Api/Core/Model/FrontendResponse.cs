// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// The API response returned to the frontend
/// </summary>
/// <param name="StatusCode">The HTTP status code to return</param>
/// <param name="Body">The body to return as a string, or null if there is no body to return</param>
public record FrontendResponse(int StatusCode, string? Body);
