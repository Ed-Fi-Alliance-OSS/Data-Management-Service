// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Core;

public static class StringExtensions
{
    public static string ToJsonError(this string error)
    {
        return JsonSerializer.Serialize(new { error });
    }
}
