// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Recognizes the Base64 text of a connection string that <see cref="ConnectionStringEncryptionService"/>
/// already encrypted. A get returns the stored cipher text as Base64 without decrypting it, so a
/// client that reads a data store, edits an unrelated field and writes the object back resubmits
/// this shape; encrypting it again would leave a value no reader can decrypt to a connection string.
///
/// It lives beside the encryption service because it describes that service's output format: the
/// two must change together.
/// </summary>
public static class ConnectionStringCipherText
{
    /// <summary>
    /// Length of the AES initialization vector <see cref="ConnectionStringEncryptionService.Encrypt"/>
    /// writes ahead of the cipher text.
    /// </summary>
    private const int IvLength = 16;

    /// <summary>
    /// AES block length in bytes. CBC output is always a whole number of blocks, so the stored value
    /// is always a whole number of blocks.
    /// </summary>
    private const int BlockLength = 16;

    /// <summary>
    /// The shortest value the encryption service can produce: the initialization vector plus the one
    /// block even a single character of plain text occupies.
    /// </summary>
    private const int MinimumLength = IvLength + BlockLength;

    /// <summary>
    /// True when the value could be output of the encryption service. A valid connection string can
    /// never match: it has to assign at least one setting, so it contains an '=' that is not Base64
    /// padding, or it is a lone "keyword=" that assigns nothing and is rejected for that instead.
    /// </summary>
    public static bool LooksLikeCipherText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Base64 text is longer than the bytes it encodes, so a buffer the length of the input
        // always holds the decoded value.
        byte[] decoded = new byte[value.Length];

        if (!Convert.TryFromBase64String(value, decoded, out int decodedLength))
        {
            return false;
        }

        return decodedLength >= MinimumLength && decodedLength % BlockLength == 0;
    }
}
