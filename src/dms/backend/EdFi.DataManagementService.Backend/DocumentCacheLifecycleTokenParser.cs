// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal static class DocumentCacheLifecycleTokenParser
{
    public static bool TryParse(string lifecycleText, out DocumentCacheLifecycleState lifecycle)
    {
        switch (lifecycleText)
        {
            case nameof(DocumentCacheLifecycleState.Disabled):
                lifecycle = DocumentCacheLifecycleState.Disabled;
                return true;
            case nameof(DocumentCacheLifecycleState.Resetting):
                lifecycle = DocumentCacheLifecycleState.Resetting;
                return true;
            case nameof(DocumentCacheLifecycleState.Rebuilding):
                lifecycle = DocumentCacheLifecycleState.Rebuilding;
                return true;
            case nameof(DocumentCacheLifecycleState.Tracking):
                lifecycle = DocumentCacheLifecycleState.Tracking;
                return true;
            default:
                lifecycle = default;
                return false;
        }
    }
}
