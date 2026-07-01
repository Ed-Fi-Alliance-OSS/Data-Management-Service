// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend.Etag;

public interface IEtagComposer
{
    /// <summary>
    /// Composes the opaque etag value (unquoted). Callers quote for HTTP headers via
    /// <see cref="EtagValue.ToHeaderValue"/>.
    /// </summary>
    string Compose(long contentVersion, VariantKey variantKey);
}

public sealed class EtagComposer : IEtagComposer
{
    public string Compose(long contentVersion, VariantKey variantKey) =>
        EtagValue.Compose(contentVersion.ToString(CultureInfo.InvariantCulture), variantKey.Value);
}
