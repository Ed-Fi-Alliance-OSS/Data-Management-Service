// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.Shared;

/// <summary>
/// Names the major version this copy is, so a test can tell which of two copies answered.
/// </summary>
/// <remarks>
/// The type's full name is identical in both copies on purpose. A test that compared type names
/// would see one type; comparing the <see cref="System.Type"/> objects themselves is what shows there
/// are two, each belonging to its own plugin's assembly.
/// </remarks>
public static class SharedMarker
{
    public const string DeclaredVersion = "1.0.0";

    public static string Describe() => "Acme.Shared 1.0.0";
}
