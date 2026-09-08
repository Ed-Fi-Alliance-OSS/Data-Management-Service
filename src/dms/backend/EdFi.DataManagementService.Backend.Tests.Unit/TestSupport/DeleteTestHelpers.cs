// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;

/// <summary>
/// Configurable <see cref="IRelationalWriteExceptionClassifier"/> double used by delete-path
/// fixtures (descriptor delete, relational document store delete). Exposes preset return values
/// plus per-method call counts so individual tests can assert both classification output AND
/// that the classifier was invoked the expected number of times. Every delete-path test fixture
/// should consume this one type to keep FK-classification behavior synchronised across fixtures.
/// </summary>
internal sealed class ConfigurableRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
{
    public bool IsForeignKeyViolationToReturn { get; set; }

    public bool IsTransientFailureToReturn { get; set; }

    public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

    public int IsForeignKeyViolationCallCount { get; private set; }

    public int IsTransientFailureCallCount { get; private set; }

    public int TryClassifyCallCount { get; private set; }

    public bool TryClassify(
        DbException exception,
        [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
    )
    {
        TryClassifyCallCount++;
        classification = ClassificationToReturn;
        return classification is not null;
    }

    public bool IsForeignKeyViolation(DbException exception)
    {
        IsForeignKeyViolationCallCount++;
        return IsForeignKeyViolationToReturn;
    }

    // Neither delete-path fixture consumes unique-violation classification today (descriptor
    // POST runs through DescriptorWriteHandler; the relational document store delete does not
    // produce unique violations). Kept as a fixed-false stub until a real path exercises it.
    public bool IsUniqueConstraintViolation(DbException exception) => false;

    public bool IsTransientFailure(DbException exception)
    {
        IsTransientFailureCallCount++;
        return IsTransientFailureToReturn;
    }
}
