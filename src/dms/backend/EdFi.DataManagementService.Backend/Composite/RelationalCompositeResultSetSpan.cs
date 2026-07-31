// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// Consumes the result-set span a logical statement declares when its rows carry no information.
/// </summary>
/// <remarks>
/// A co-batched authorization run emits one result set per planned check and communicates a denial by
/// aborting the command, not by returning a row. The span must still be walked: it keeps the decoder's
/// position aligned with the declared <c>ResultSetCount</c> so every later statement's ordinal stays
/// correct, and it forces a provider that raises during row streaming rather than at the result-set
/// boundary to raise inside this statement's span.
/// </remarks>
internal static class RelationalCompositeResultSetSpan
{
    public static async Task<object?> ConsumeAsync(
        DbDataReader reader,
        int resultSetCount,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(resultSetCount, 1);

        for (var resultSetIndex = 0; resultSetIndex < resultSetCount; resultSetIndex++)
        {
            if (resultSetIndex > 0 && !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Expected {resultSetCount} authorization result sets but the provider produced "
                        + $"{resultSetIndex}."
                );
            }

            bool hasRow;

            do
            {
                hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            } while (hasRow);
        }

        return null;
    }
}
