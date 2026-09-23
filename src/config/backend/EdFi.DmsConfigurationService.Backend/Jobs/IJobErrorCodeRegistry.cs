// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The registered job error codes: the infrastructure codes plus consumer codes added at startup
/// (spec D-7). Lookup is ordinal.
/// </summary>
public interface IJobErrorCodeRegistry
{
    bool TryGet(string code, [NotNullWhen(true)] out JobErrorCode? errorCode);
}
