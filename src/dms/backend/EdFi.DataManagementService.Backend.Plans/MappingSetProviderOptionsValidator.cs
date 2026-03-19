// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates <see cref="MappingSetProviderOptions"/> at startup, rejecting
/// internally contradictory configuration.
/// </summary>
public sealed class MappingSetProviderOptionsValidator : IValidateOptions<MappingSetProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, MappingSetProviderOptions options)
    {
        if (options.Required && !options.Enabled)
        {
            return ValidateOptionsResult.Fail(
                "MappingPacks:Required is true but MappingPacks:Enabled is false. "
                    + "Packs cannot be required when pack loading is disabled."
            );
        }

        if (options.Enabled && !options.Required && !options.AllowRuntimeCompileFallback)
        {
            return ValidateOptionsResult.Fail(
                "MappingPacks:Enabled is true and MappingPacks:AllowRuntimeCompileFallback is false, "
                    + "but MappingPacks:Required is false. "
                    + "Set Required=true or enable AllowRuntimeCompileFallback."
            );
        }

        if (!Enum.IsDefined(options.CacheMode))
        {
            return ValidateOptionsResult.Fail(
                $"MappingPacks:CacheMode value '{options.CacheMode}' is not a supported cache mode."
            );
        }

        return ValidateOptionsResult.Success;
    }
}
