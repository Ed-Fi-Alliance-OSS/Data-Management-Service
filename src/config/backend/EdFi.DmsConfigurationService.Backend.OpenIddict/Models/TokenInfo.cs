// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents token information retrieved from the OpenIddict store.
/// </summary>
public class TokenInfo
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreationDate { get; set; }
    public DateTimeOffset? ExpirationDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public DateTimeOffset? RedemptionDate { get; set; }
}
