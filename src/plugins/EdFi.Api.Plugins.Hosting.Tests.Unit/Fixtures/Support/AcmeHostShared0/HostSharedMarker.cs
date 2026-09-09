// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.HostShared;

/// <summary>Names the version a plugin shipped, so a test can tell which copy was served.</summary>
public class HostSharedMarker
{
    public const string DeclaredVersion = "0.5.0";

    public static string Describe() => "Acme.HostShared 0.5.0";
}
