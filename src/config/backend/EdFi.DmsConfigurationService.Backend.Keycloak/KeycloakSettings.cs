// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class KeycloakSettings
{
    public required string Url { get; set; }
    public required string Realm { get; set; }
}

public class KeycloakSettingsValidator : IValidateOptions<KeycloakSettings>
{
    public ValidateOptionsResult Validate(string? name, KeycloakSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return ValidateOptionsResult.Fail("Missing required KeycloakSettings value: Url");
        }
        if (string.IsNullOrWhiteSpace(options.Realm))
        {
            return ValidateOptionsResult.Fail("Missing required KeycloakSettings value: Realm");
        }
        return ValidateOptionsResult.Success;
    }
}
