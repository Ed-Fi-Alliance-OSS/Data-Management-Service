// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

public class IdentitySettings
{
    public required string Authority { get; set; }
    public bool RequireHttpsMetadata { get; set; }
    public bool EnforceAuthorization { get; set; }
    public required string Audience { get; set; }
    public required string RoleClaimType { get; set; }
    public required string ClientRole { get; set; }
}

public class IdentitySettingsValidator : IValidateOptions<IdentitySettings>
{
    public ValidateOptionsResult Validate(string? name, IdentitySettings options)
    {
        if (options.EnforceAuthorization)
        {
            if (string.IsNullOrWhiteSpace(options.Authority))
            {
                return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Authority");
            }
            if (string.IsNullOrEmpty(options.Audience))
            {
                return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Audience");
            }
            if (string.IsNullOrEmpty(options.RoleClaimType))
            {
                return ValidateOptionsResult.Fail("Missing required IdentitySettings value: RoleClaimType");
            }
            if (string.IsNullOrEmpty(options.ClientRole))
            {
                return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ClientRole");
            }
        }
        return ValidateOptionsResult.Success;
    }
}
