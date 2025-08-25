// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents application information retrieved from the OpenIddict store.
/// </summary>
public class ApplicationInfo
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string[] RedirectUris { get; set; } = [];
    public string[] PostLogoutRedirectUris { get; set; } = [];
    public string[] Permissions { get; set; } = [];
    public string[] Requirements { get; set; } = [];
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string[] Scopes { get; set; } = [];
    public string ProtocolMappers { get; set; } = String.Empty;
}
