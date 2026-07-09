// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// The representation context needed to compose a served etag. <see cref="ProfileName"/> is
/// <see langword="null"/> when no readable profile applies. Descriptor responses use
/// <c>Format: Json</c> and <c>LinksEnabled: false</c>; <see cref="ProfileName"/> is populated only
/// when the descriptor response is governed by a readable profile.
/// </summary>
public readonly record struct ServedEtagContext(
    string EffectiveSchemaHash,
    ResponseFormat Format,
    string? ProfileName,
    bool LinksEnabled,
    long ContentVersion
);

/// <summary>
/// Single home for "compose the served etag". Wraps <see cref="VariantKeyFactory"/> +
/// <see cref="ProfileVariantCode"/> + <see cref="EtagComposer"/> so callers supply only context.
/// </summary>
public interface IServedEtagComposer
{
    string Compose(ServedEtagContext context);
}

public sealed class ServedEtagComposer : IServedEtagComposer
{
    public string Compose(ServedEtagContext context) =>
        EtagComposer.Compose(
            context.ContentVersion,
            VariantKeyFactory.Create(
                context.EffectiveSchemaHash,
                context.Format,
                ProfileVariantCode.Of(context.ProfileName),
                context.LinksEnabled
            )
        );
}
