// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The compiled page-selection statement one measured request executed: its SQL text, the
/// bound parameter values, and the lowercase SHA-256 of the text that artifacts carry so a
/// later story can mechanically confirm the traditional SQL never changed.
/// </summary>
public sealed record PageSelectionQueryCapture(
    string PageDocumentIdSql,
    IReadOnlyDictionary<string, object?> ParameterValues,
    string Sha256
);

/// <summary>
/// Extracts the page-selection capture from the hydration keysets the query recorder observed
/// inside one request window. Exactly one Query keyset is the only acceptable observation.
/// </summary>
public static class PageSelectionCapture
{
    public static PageSelectionQueryCapture ExtractSingleQuery(IReadOnlyList<PageKeysetSpec> newKeysets)
    {
        if (newKeysets.Count != 1)
        {
            throw new PerfObservationException(
                $"Expected exactly one hydration keyset in the request window; observed {newKeysets.Count}."
            );
        }

        if (newKeysets[0] is not PageKeysetSpec.Query query)
        {
            throw new PerfObservationException(
                $"Expected a Query hydration keyset; observed {newKeysets[0].GetType().Name}."
            );
        }

        string sql = query.Plan.PageDocumentIdSql;
        return new PageSelectionQueryCapture(sql, query.ParameterValues, Sha256Lowercase(sql));
    }

    public static string Sha256Lowercase(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
