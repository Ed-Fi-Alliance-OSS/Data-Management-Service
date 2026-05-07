// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Backend;

internal static class WritePreconditionFactory
{
    private const string IfMatchHeaderName = "If-Match";

    public static WritePrecondition Create(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return headers.TryGetValue(IfMatchHeaderName, out var ifMatchValue)
            ? new WritePrecondition.IfMatch(ifMatchValue ?? string.Empty)
            : new WritePrecondition.None();
    }
}
