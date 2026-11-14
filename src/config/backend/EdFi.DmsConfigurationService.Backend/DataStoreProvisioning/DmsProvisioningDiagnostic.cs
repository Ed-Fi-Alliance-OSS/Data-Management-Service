// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Everything a provisioning failure records about its cause (AC 11, AC 12): the exception type chain, a
/// vetted provider error code when one exists, and the fixed operation name that failed. It never
/// carries an exception message, a stack trace, process output, a connection string, or any value taken
/// from a request, file, or row.
/// </summary>
public sealed record DmsProvisioningDiagnostic
{
    /// <param name="exceptionTypeChain">The <c>/</c>-joined full type names of the exception and its inner
    /// exceptions, outermost first, or empty when no exception was involved.</param>
    /// <param name="providerErrorCode">A PostgreSQL SQLSTATE (<c>^[0-9A-Z]{5}$</c>) or a SQL Server error
    /// number, or null. Anything else is rejected so free text can never ride this field.</param>
    /// <param name="operation">A fixed operation name chosen by the caller, such as <c>RenameIntoTarget</c>.</param>
    public DmsProvisioningDiagnostic(string exceptionTypeChain, string? providerErrorCode, string operation)
    {
        ArgumentNullException.ThrowIfNull(exceptionTypeChain);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (providerErrorCode is not null && !IsValidProviderErrorCode(providerErrorCode))
        {
            throw new ArgumentException(
                "The provider error code must be a five-character SQLSTATE or a SQL Server error number.",
                nameof(providerErrorCode)
            );
        }

        ExceptionTypeChain = exceptionTypeChain;
        ProviderErrorCode = providerErrorCode;
        Operation = operation;
    }

    public string ExceptionTypeChain { get; }

    public string? ProviderErrorCode { get; }

    public string Operation { get; }

    /// <summary>A diagnostic for a failure no exception caused, such as a validation verdict.</summary>
    public static DmsProvisioningDiagnostic ForOperation(string operation) =>
        new(string.Empty, null, operation);

    /// <summary>A diagnostic for a failure an exception caused; only type names are read from it.</summary>
    public static DmsProvisioningDiagnostic From(
        Exception exception,
        string operation,
        string? providerErrorCode = null
    ) => new(TypeChain(exception), providerErrorCode, operation);

    /// <summary>The <c>/</c>-joined full type names of <paramref name="exception"/> and each inner exception, outermost first.</summary>
    public static string TypeChain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<string> typeNames = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            typeNames.Add(current.GetType().FullName ?? current.GetType().Name);
        }

        return string.Join('/', typeNames);
    }

    /// <summary>A five-character SQLSTATE (digits and uppercase letters) or a SQL Server error number (optionally negative, up to ten digits).</summary>
    public static bool IsValidProviderErrorCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (
            code.Length == 5
            && code.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character))
        )
        {
            return true;
        }

        string digits = code.StartsWith('-') ? code[1..] : code;
        return digits.Length is >= 1 and <= 10 && digits.All(char.IsAsciiDigit);
    }
}
