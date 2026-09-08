// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Extraction;

public sealed class ReferenceExtractionValidationException : Exception
{
    public ReferenceExtractionValidationException(WriteValidationFailure[] validationFailures)
        : base("Data validation failed. See 'validationErrors' for details.")
    {
        ValidationFailures =
            validationFailures?.Length > 0
                ? validationFailures
                : throw new ArgumentException(
                    "At least one validation failure must be supplied.",
                    nameof(validationFailures)
                );
    }

    public WriteValidationFailure[] ValidationFailures { get; }
}
