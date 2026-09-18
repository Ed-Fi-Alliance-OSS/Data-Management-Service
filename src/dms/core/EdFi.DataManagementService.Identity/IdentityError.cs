// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// A single failure a provider reports for an <see cref="IdentityResultStatus.InvalidProperties"/>
/// result. DMS projects <see cref="IdentityError"/> entries only for that status; entries returned
/// alongside any other status are ignored.
/// </summary>
public sealed record IdentityError
{
    /// <summary>
    /// The failure message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The JSONPath the failure applies to, rooted at <c>$</c> (for example <c>$.propertyName</c>),
    /// with an array item addressed as <c>$[n].property</c>. Null or blank routes the message to the
    /// response's document-level <c>errors</c> collection instead of a path-scoped entry.
    /// </summary>
    public string? Path { get; init; }
}
