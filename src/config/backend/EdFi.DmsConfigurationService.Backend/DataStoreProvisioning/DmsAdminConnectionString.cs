// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// One resolution of <c>AdminConnectionString</c>: immutable, so an operation that captured it connects to the
/// same server with the same credentials from its lock to its last cleanup, whatever another operation resolves
/// meanwhile. <see cref="ToString"/> never renders the value.
/// </summary>
public sealed class DmsAdminConnectionString
{
    public DmsAdminConnectionString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>The resolved connection string; never logged.</summary>
    public string Value { get; }

    public override string ToString() => "[administrative connection string]";
}
