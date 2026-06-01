// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Thrown when a SQL Server namespace authorization would require more than the supported number of
/// parameterized OR-chain LIKE clauses. The repository layer maps this to a 500 Security Configuration
/// Error so the client can diagnose the configuration limit without seeing an internal SQL error.
/// </summary>
public sealed class NamespacePrefixLimitExceededException : InvalidOperationException
{
    public const int MssqlScalarParameterLimit = 2000;

    public NamespacePrefixLimitExceededException(int prefixCount)
        : base(BuildMessage(prefixCount))
    {
        PrefixCount = prefixCount;
    }

    public int PrefixCount { get; }

    private static string BuildMessage(int prefixCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The API client has {prefixCount} namespace prefixes, which exceeds the SQL Server limit of {MssqlScalarParameterLimit} parameterized LIKE clauses for NamespaceBased authorization."
        );
}
