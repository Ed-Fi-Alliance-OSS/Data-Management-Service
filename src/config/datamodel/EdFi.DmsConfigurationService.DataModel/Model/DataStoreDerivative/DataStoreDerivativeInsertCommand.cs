// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;

/// <summary>
/// Command to insert a new data store derivative
/// </summary>
public class DataStoreDerivativeInsertCommand
{
    /// <summary>
    /// The parent data store ID
    /// </summary>
    public long DataStoreId { get; set; }

    /// <summary>
    /// The type of derivative: "ReadReplica" or "Snapshot"
    /// </summary>
    public string DerivativeType { get; set; } = "";

    /// <summary>
    /// The connection string for the derivative instance
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Validator for DataStoreDerivativeInsertCommand
    /// </summary>
    public class Validator : AbstractValidator<DataStoreDerivativeInsertCommand>
    {
        private static readonly string[] ValidDerivativeTypes = ["ReadReplica", "Snapshot"];

        public Validator()
        {
            RuleFor(x => x.DataStoreId).GreaterThan(0).WithMessage("DataStoreId must be greater than 0.");

            RuleFor(x => x.DerivativeType)
                .NotEmpty()
                .WithMessage("DerivativeType is required.")
                .MaximumLength(50)
                .WithMessage("DerivativeType must be 50 characters or fewer.")
                .Must(type => ValidDerivativeTypes.Contains(type))
                .WithMessage("DerivativeType must be either 'ReadReplica' or 'Snapshot'.");

            RuleFor(x => x.ConnectionString)
                .MaximumLength(1000)
                .WithMessage("ConnectionString must be 1000 characters or fewer.");
        }
    }
}
