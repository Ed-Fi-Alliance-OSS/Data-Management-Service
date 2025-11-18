// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Core;

public static partial class UtilityService
{
    // Matches all of the following sample expressions:
    // /ed-fi/sections
    // /ed-fi/sections/
    // /ed-fi/sections/idValue
    [GeneratedRegex(@"\/(?<projectNamespace>[^/]+)\/(?<endpointName>[^/]+)(\/|$)((?<documentUuid>[^/]*$))?")]
    public static partial Regex PathExpressionRegex();

    // Regex for a UUID string - does not enforce any particular UUID version
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    public static partial Regex UuidRegex();

    //Prevent string contain space characters
    [GeneratedRegex("(\"(?:[^\"\\\\]|\\\\.)*\")|\\s+")]
    public static partial Regex MinifyRegex();

    //Use to avoid HTML escaping in output message that we construct
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
