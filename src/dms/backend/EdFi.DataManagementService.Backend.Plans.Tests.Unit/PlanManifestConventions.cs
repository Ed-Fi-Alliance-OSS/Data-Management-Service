// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class PlanManifestConventions
{
    public static string ToManifestDialect(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Mssql => "mssql",
            SqlDialect.Pgsql => "pgsql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    public static string ComputeNormalizedSha256(string value)
    {
        var normalizedBytes = Encoding.UTF8.GetBytes(PlanJsonCanonicalization.NormalizeMultilineText(value));

        return Convert.ToHexStringLower(SHA256.HashData(normalizedBytes));
    }
}
