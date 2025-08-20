// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Service for handling claims uploads and validations
/// </summary>
public interface IClaimsUploadService
{
    /// <summary>
    /// Uploads and validates claims from JSON content
    /// </summary>
    /// <param name="claimsJson">The claims JSON to upload</param>
    /// <returns>Status of the upload operation</returns>
    Task<ClaimsLoadStatus> UploadClaimsAsync(JsonNode claimsJson);

    /// <summary>
    /// Reloads claims from the configured source
    /// </summary>
    /// <returns>Status of the reload operation</returns>
    Task<ClaimsLoadStatus> ReloadClaimsAsync();
}
