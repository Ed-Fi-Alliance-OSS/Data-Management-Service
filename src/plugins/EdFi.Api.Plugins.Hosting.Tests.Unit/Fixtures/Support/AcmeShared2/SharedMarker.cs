// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.Shared;

/// <summary>
/// The second major's copy of the same type, declaring the same full name at a different version.
/// </summary>
public static class SharedMarker
{
    public const string DeclaredVersion = "2.0.0";

    public static string Describe() => "Acme.Shared 2.0.0";
}
