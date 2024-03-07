// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// Provides hash generation in support of ReferentialIds
/// </summary>
public static class HashUtility
{
    /// <summary>
    /// Converts Base64 to Base64Url by character replacement and truncation of padding.
    /// '+' becomes '-', '/' becomes '_', and any trailing '=' are removed.
    /// See https://datatracker.ietf.org/doc/html/rfc4648#section-5
    /// </summary>
    private static string ToBase64Url(byte[] source)
    {
        return Convert.ToBase64String(source).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Hashes a string with SHAKE256, returning a Base64Url hash with the specified length given in bytes
    /// </summary>
    public static string ToHash(string data, int lengthInBytes)
    {
        return ToBase64Url(Shake256.HashData(Encoding.UTF8.GetBytes(data), lengthInBytes));
    }
}
