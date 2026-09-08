// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalWriteRequestValidationException : Exception
{
    public RelationalWriteRequestValidationException(WriteValidationFailure[] validationFailures)
        : base("Data validation failed. See 'validationErrors' for details.")
    {
        ArgumentNullException.ThrowIfNull(validationFailures);

        if (validationFailures.Length == 0)
        {
            throw new ArgumentException(
                "At least one validation failure must be supplied.",
                nameof(validationFailures)
            );
        }

        ValidationFailures = validationFailures;
    }

    public WriteValidationFailure[] ValidationFailures { get; }

    public static RelationalWriteRequestValidationException ForPath(string path, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new([new WriteValidationFailure(new JsonPath(path), message)]);
    }
}
