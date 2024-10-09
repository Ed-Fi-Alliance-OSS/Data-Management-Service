// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

public class ConnectionStrings
{
    public required string DatabaseConnection { get; set; }
}

public class ConnectionStringsValidator : IValidateOptions<ConnectionStrings>
{
    public ValidateOptionsResult Validate(string? name, ConnectionStrings options)
    {
        return string.IsNullOrWhiteSpace(options.DatabaseConnection)
            ? ValidateOptionsResult.Fail("Missing required ConnectionStrings value: DatabaseConnection")
            : ValidateOptionsResult.Success;
    }
}
