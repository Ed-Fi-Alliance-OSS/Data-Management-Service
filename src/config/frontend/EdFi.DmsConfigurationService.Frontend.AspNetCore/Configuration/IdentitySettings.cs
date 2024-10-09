// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

public class IdentitySettings
{
    public required string Authority { get; set; }
    public required string IdentityServer { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public required string Realm { get; set; }
    public bool RequireHttpsMetadata { get; set; }
    public bool AllowRegistration { get; set; }
    public required string Audience { get; set; }
    public required string RoleClaimType { get; set; }
    public required string ServiceRole { get; set; }
}

public class IdentitySettingsValidator : IValidateOptions<IdentitySettings>
{
    public ValidateOptionsResult Validate(string? name, IdentitySettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Authority");
        }

        if (string.IsNullOrWhiteSpace(options.IdentityServer))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: IdentityServer");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ClientId");
        }
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ClientSecret");
        }
        if (string.IsNullOrWhiteSpace(options.Realm))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Realm");
        }
        if (string.IsNullOrEmpty(options.Audience))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Audience");
        }
        if (string.IsNullOrEmpty(options.RoleClaimType))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: RoleClaimType");
        }
        if (string.IsNullOrEmpty(options.ServiceRole))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ServiceRole");
        }
        return ValidateOptionsResult.Success;
    }
}
