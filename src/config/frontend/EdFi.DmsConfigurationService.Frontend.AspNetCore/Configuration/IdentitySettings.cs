// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

public class IdentitySettings
{
    /// <summary>
    /// Applied when the section omits RequireHttpsMetadata. The startup diagnostic falls back to the same
    /// value, so a missing key never makes the warning and authentication disagree.
    /// </summary>
    internal const bool RequireHttpsMetadataDefault = true;

    public required string Authority { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public bool RequireHttpsMetadata { get; set; } = RequireHttpsMetadataDefault;
    public bool AllowRegistration { get; set; }
    public required string Audience { get; set; }
    public required string RoleClaimType { get; set; }
    public required string ConfigServiceRole { get; set; }
    public required string ClientRole { get; set; }
}

public class IdentitySettingsValidator(
    IOptions<ClientSecretValidationOptions>? clientSecretValidationOptionsAccessor = null
) : IValidateOptions<IdentitySettings>
{
    private readonly ClientSecretValidationOptions _clientSecretValidationOptions =
        clientSecretValidationOptionsAccessor?.Value ?? new();

    public ValidateOptionsResult Validate(string? name, IdentitySettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Authority");
        }
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ClientId");
        }
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ClientSecret");
        }
        if (!ClientSecretValidation.IsWithinLengthRange(options.ClientSecret, _clientSecretValidationOptions))
        {
            return ValidateOptionsResult.Fail(
                ClientSecretValidation.BuildLengthErrorMessage(
                    "IdentitySettings value: ClientSecret",
                    _clientSecretValidationOptions
                )
            );
        }
        if (
            !System.Text.RegularExpressions.Regex.IsMatch(
                options.ClientSecret,
                ClientSecretValidation.BuildComplexityPattern(_clientSecretValidationOptions)
            )
        )
        {
            return ValidateOptionsResult.Fail(
                ClientSecretValidation.BuildComplexityErrorMessage(_clientSecretValidationOptions)
            );
        }
        if (string.IsNullOrEmpty(options.Audience))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: Audience");
        }
        if (string.IsNullOrEmpty(options.RoleClaimType))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: RoleClaimType");
        }
        if (string.IsNullOrEmpty(options.ConfigServiceRole))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ConfigServiceRole");
        }
        if (string.IsNullOrEmpty(options.ClientRole))
        {
            return ValidateOptionsResult.Fail("Missing required IdentitySettings value: ClientRole");
        }
        return ValidateOptionsResult.Success;
    }
}
