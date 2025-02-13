// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Security;

public enum ActionType
{
    Create,
    Read,
    Update,
    Delete,
    Unknown,
}

internal static class ActionResolver
{
    private static readonly Dictionary<RequestMethod, ActionType> _actionMappings = new()
    {
        { RequestMethod.POST, ActionType.Create },
        { RequestMethod.GET, ActionType.Read },
        { RequestMethod.PUT, ActionType.Update },
        { RequestMethod.DELETE, ActionType.Delete },
    };

    public static ActionType Resolve(RequestMethod requestMethod)
    {
        return _actionMappings.TryGetValue(requestMethod, out var translatedAction)
            ? translatedAction
            : ActionType.Unknown;
    }
}
