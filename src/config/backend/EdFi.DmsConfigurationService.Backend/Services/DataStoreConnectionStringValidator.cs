// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Validates a submitted data store connection string, deferring the parse itself to the provider
/// backend. Everything except the parse is engine independent, so it is settled here and each
/// provider supplies only its own builder.
/// </summary>
public abstract class DataStoreConnectionStringValidator : IDataStoreConnectionStringValidator
{
    /// <summary>
    /// The messages are engine independent on purpose: the same request has to produce the same
    /// response whichever engine is configured, and no message repeats the submitted value.
    /// </summary>
    public const string EmptyMessage = "'Connection String' must not be empty when provided.";

    public const string CipherTextMessage =
        "'Connection String' must be a plaintext connection string. The value provided appears to be an encrypted value previously returned by this API.";

    public const string MalformedMessage =
        "'Connection String' is not a valid connection string for the configured database engine.";

    public const string NoSettingsMessage =
        "'Connection String' must specify at least one connection setting.";

    public ConnectionStringValidationResult Validate(string? connectionString)
    {
        // Not provided. Whether that is acceptable belongs to the caller, so it is not a failure here.
        if (connectionString is null)
        {
            return new ConnectionStringValidationResult.Valid();
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ConnectionStringValidationResult.Invalid(EmptyMessage);
        }

        // Ahead of the parse: cipher text whose Base64 ends in a single '=' parses as a keyword with
        // an empty value under SQL Server, so the parse alone would accept some of it. This test
        // covers every length the encryption service can emit, and it names the mistake precisely.
        if (ConnectionStringCipherText.LooksLikeCipherText(connectionString))
        {
            return new ConnectionStringValidationResult.Invalid(CipherTextMessage);
        }

        DbConnectionStringBuilder builder;

        try
        {
            builder = CreateBuilder(connectionString);
        }
        catch (Exception)
        {
            // Any throw means the provider cannot read the submitted text, which is the answer we
            // need. The builders parse caller input and have no infrastructure failure mode, and the
            // types they throw are not part of their contract: Npgsql raises KeyNotFoundException for
            // an unrecognized keyword carrying an empty value where malformed text raises
            // ArgumentException, so listing types would turn caller input into a 500. The exception
            // is deliberately neither logged nor surfaced, because its message repeats the submitted
            // value.
            return new ConnectionStringValidationResult.Invalid(MalformedMessage);
        }

        // A value can parse and still assign nothing, as ";;;" and "keyword=" do. That is never a
        // connection the reader can open.
        return builder.Count == 0
            ? new ConnectionStringValidationResult.Invalid(NoSettingsMessage)
            : new ConnectionStringValidationResult.Valid();
    }

    /// <summary>
    /// Parses the value with the provider's own connection string builder, throwing when the provider
    /// does not accept it.
    /// </summary>
    protected abstract DbConnectionStringBuilder CreateBuilder(string connectionString);
}
