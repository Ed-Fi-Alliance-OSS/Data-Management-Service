// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.Private;

/// <summary>Something only a plugin that ships this assembly can reach.</summary>
public static class PrivateMarker
{
    public static string Describe() => "Acme.Private 1.0.0";
}
