// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend
{
    public class DatabaseOptions
    {
        public required string DatabaseConnection { get; set; }
        public required string EncryptionKey { get; set; }
    }

    public class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
    {
        public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.DatabaseConnection))
            {
                return ValidateOptionsResult.Fail(
                    "Missing required ConnectionStrings value: DatabaseConnection"
                );
            }

            if (string.IsNullOrWhiteSpace(options.EncryptionKey))
            {
                return ValidateOptionsResult.Fail("Missing required ConnectionStrings value: EncryptionKey");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
