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

        if (options.FailureCooldownSeconds < 0)
        {
            return ValidateOptionsResult.Fail("MappingPacks:FailureCooldownSeconds must not be negative.");
        }

        if (!Enum.IsDefined(options.CacheMode))
        {
            return ValidateOptionsResult.Fail(
                $"MappingPacks:CacheMode value '{options.CacheMode}' is not a supported cache mode."
            );
        }

        // TODO(DMS-968): When a real pack store is wired up, validate that
        // RootPath is non-empty when Enabled is true.

        return ValidateOptionsResult.Success;
    }
}
