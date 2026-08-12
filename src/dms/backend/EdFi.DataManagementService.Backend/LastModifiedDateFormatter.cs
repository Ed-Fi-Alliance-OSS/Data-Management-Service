// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend;

internal static class LastModifiedDateFormatter
{
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    public static string Format(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.ToUniversalTime().ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

    public static string Format(DateTime lastModifiedAt) =>
        NormalizeUtcDateTime(lastModifiedAt).ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

    private static DateTime NormalizeUtcDateTime(DateTime dateTime) =>
        dateTime.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeKind.Utc => dateTime,
            _ => dateTime.ToUniversalTime(),
        };
}
