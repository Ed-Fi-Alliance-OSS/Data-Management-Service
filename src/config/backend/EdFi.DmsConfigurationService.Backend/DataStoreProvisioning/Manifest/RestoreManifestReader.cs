// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// Reads one <c>restore-manifest.json</c> document strictly: the text is parsed with the trust
/// contract's document rules (duplicate member names rejected, every name and string decoded once, no
/// comments or trailing commas), then validated by <see cref="RestoreManifestShapeValidator"/> against every
/// rule of the delivered <c>Assert-RestoreManifestShape</c>, with unknown members rejected and numbers required
/// to be JSON numbers. Any defect throws <see cref="RestoreManifestException"/>; the document text is never
/// echoed.
/// </summary>
public static class RestoreManifestReader
{
    private static readonly byte[] _utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    public static RestoreManifest Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RestoreManifestException("The restore manifest is empty.");
        }

        if (!TrustContract.TryParse(json, out JsonDocument? document, out TrustContract.ParseFailure failure))
        {
            throw ParseException(failure);
        }

        using (document)
        {
            return RestoreManifestShapeValidator.Validate(document.RootElement);
        }
    }

    /// <summary>Reads the manifest from its UTF-8 bytes, as extracted from a package; a leading byte order mark is skipped, as the delivered consumer's file read does.</summary>
    public static RestoreManifest Read(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Span.StartsWith(_utf8ByteOrderMark))
        {
            utf8Json = utf8Json[_utf8ByteOrderMark.Length..];
        }

        if (utf8Json.IsEmpty)
        {
            throw new RestoreManifestException("The restore manifest is empty.");
        }

        if (
            !TrustContract.TryParse(
                utf8Json,
                out JsonDocument? document,
                out TrustContract.ParseFailure failure
            )
        )
        {
            throw ParseException(failure);
        }

        using (document)
        {
            return RestoreManifestShapeValidator.Validate(document.RootElement);
        }
    }

    private static RestoreManifestException ParseException(TrustContract.ParseFailure failure) =>
        new(
            failure == TrustContract.ParseFailure.MalformedText
                ? "The restore manifest contains text that is not well-formed Unicode."
                : "The restore manifest is not valid JSON."
        );
}
