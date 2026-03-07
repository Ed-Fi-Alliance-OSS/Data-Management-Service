// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Thrown when the provisioned dms.EffectiveSchema content is present but malformed.
/// This represents a per-database fail-fast condition rather than a transient read failure.
/// </summary>
public sealed class DatabaseFingerprintValidationException : InvalidOperationException
{
    public DatabaseFingerprintValidationException(string message)
        : base(message) { }

    public DatabaseFingerprintValidationException(string message, Exception innerException)
        : base(message, innerException) { }
}
