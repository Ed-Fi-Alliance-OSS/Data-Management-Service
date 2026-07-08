// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Bridges the backend etag inputs (<c>ContentVersion</c> + <see cref="VariantKey"/>) to the opaque
/// wire value produced by <see cref="EtagValue.Compose(string, string)"/>. This lives in the backend
/// layer because <see cref="EtagValue"/> (in Core) cannot reference the backend-only
/// <see cref="VariantKey"/> type; keeping the conversion here gives it a single home. The result is
/// unquoted; callers quote for HTTP headers via <see cref="EtagValue.ToHeaderValue"/>.
/// </summary>
public static class EtagComposer
{
    // ContentVersion is emitted as an opaque, culture-invariant decimal string; it is never parsed
    // or compared numerically downstream (RFC 7232 §2.3).
    public static string Compose(long contentVersion, VariantKey variantKey) =>
        EtagValue.Compose(contentVersion.ToString(CultureInfo.InvariantCulture), variantKey.Value);
}
