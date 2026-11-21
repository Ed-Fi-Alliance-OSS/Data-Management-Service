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
    /// <summary>
    /// A single registered code. Composite codes are not found here: they resolve only through
    /// <see cref="TryCompose"/>.
    /// </summary>
    bool TryGet(string code, [NotNullWhen(true)] out JobErrorCode? errorCode);

    /// <summary>
    /// The public message of a composite failure: the registered message of <paramref name="primaryCode"/>, then
    /// <c>" Problem categories: "</c>, the distinct <paramref name="categoryCodes"/> in first-occurrence order joined
    /// by <c>", "</c>, and <c>"."</c>; then each category's registered message, in the same order, while the text stays
    /// within 1000 characters. Only registered text is used. Returns false, and no message, when the primary is not a
    /// registered composite, the list is empty, or any category is outside the composite's allowed categories.
    /// </summary>
    bool TryCompose(
        string primaryCode,
        IReadOnlyList<string> categoryCodes,
        [NotNullWhen(true)] out string? message
    );
}
