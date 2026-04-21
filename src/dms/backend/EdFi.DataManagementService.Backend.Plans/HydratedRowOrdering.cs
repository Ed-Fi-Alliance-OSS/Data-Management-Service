// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

internal static class HydratedRowOrdering
{
    public static void EnsureOrdinalOrder<T>(List<T> rows, Func<T, int> resolveOrdinal)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(resolveOrdinal);

        if (rows.Count < 2)
        {
            return;
        }

        var previousOrdinal = resolveOrdinal(rows[0]);

        for (var index = 1; index < rows.Count; index++)
        {
            var currentOrdinal = resolveOrdinal(rows[index]);

            if (currentOrdinal < previousOrdinal)
            {
                rows.Sort((left, right) => resolveOrdinal(left).CompareTo(resolveOrdinal(right)));
                return;
            }

            previousOrdinal = currentOrdinal;
        }
    }
}
