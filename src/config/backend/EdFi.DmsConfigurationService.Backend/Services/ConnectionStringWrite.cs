// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Whether an update writes the connection string it was handed, or leaves the stored one alone.
///
/// A get returns the stored cipher text and the write path refuses cipher text, so a client that
/// reads a data store and writes it back cannot resend the value it read. Leaving the field out is
/// how such a client keeps the stored connection string, which is why a null value means "not
/// provided" here rather than "clear this".
///
/// No submitted value clears a stored connection string through the API: an empty or whitespace
/// value is rejected before an update reaches a repository.
///
/// One rule for both resources on both engines, so the four update paths cannot drift apart.
/// </summary>
public static class ConnectionStringWrite
{
    public static bool PreservesExistingValue(string? submittedConnectionString) =>
        submittedConnectionString is null;
}
