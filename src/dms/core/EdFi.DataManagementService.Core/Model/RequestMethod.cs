// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Model;

internal enum RequestMethod
{
    POST,
    GET,
    PUT,
    DELETE,

    /// <summary>
    /// A request whose HTTP method is not one of the supported verbs above. The actual
    /// method name is carried on RequestInfo.UnsupportedMethodName.
    /// </summary>
    UNSUPPORTED,
}
