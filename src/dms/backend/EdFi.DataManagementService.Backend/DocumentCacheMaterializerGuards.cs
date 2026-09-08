// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

internal static class DocumentCacheMaterializerGuards
{
    public static long RequirePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }

        return value;
    }

    public static void RequireCacheAheadLatchVersions(long sourceContentVersion, long cacheContentVersion)
    {
        RequirePositive(sourceContentVersion, nameof(sourceContentVersion));
        RequirePositive(cacheContentVersion, nameof(cacheContentVersion));

        if (cacheContentVersion <= sourceContentVersion)
        {
            throw new ArgumentException(
                "Cache-ahead latch outcomes require cache ContentVersion to be greater than source ContentVersion.",
                nameof(cacheContentVersion)
            );
        }
    }

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, message);
        }

        return value;
    }
}
