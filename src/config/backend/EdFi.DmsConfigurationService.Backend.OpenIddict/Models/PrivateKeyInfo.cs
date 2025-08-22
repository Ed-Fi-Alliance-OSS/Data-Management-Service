// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents private key information retrieved from the OpenIddict store.
/// </summary>
public class PrivateKeyInfo
{
    public string PrivateKey { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
}
