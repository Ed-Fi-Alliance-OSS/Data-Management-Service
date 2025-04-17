// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader
{
    public class ConfigurationServiceSettings
    {
        public required string BaseUrl { get; set; }
        public required string ClientId { get; set; }
        public required string ClientSecret { get; set; }
        public required string Scope { get; set; }
        public required int CacheExpirationMinutes { get; set; }
    }

    public class ConfigurationServiceSettingsValidator : IValidateOptions<ConfigurationServiceSettings>
    {
        public ValidateOptionsResult Validate(string? name, ConfigurationServiceSettings options)
        {
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: BaseUrl");
            }

            if (string.IsNullOrWhiteSpace(options.ClientId))
            {
                return ValidateOptionsResult.Fail(
                    "Missing required ConfigurationServiceSettings value: ClientId"
                );
            }
            if (string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                return ValidateOptionsResult.Fail(
                    "Missing required ConfigurationServiceSettings value: ClientSecret"
                );
            }
            if (string.IsNullOrWhiteSpace(options.Scope))
            {
                return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: Scope");
            }
            if (options.CacheExpirationMinutes > 0)
            {
                return ValidateOptionsResult.Fail(
                    "Missing required ConfigurationServiceSettings value: CacheExpirationMinutes"
                );
            }
            return ValidateOptionsResult.Success;
        }
    }
}
