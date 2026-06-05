// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;

/// <summary>
/// Command to update an existing data store derivative
/// </summary>
public class DataStoreDerivativeUpdateCommand
{
    /// <summary>
    /// The unique identifier of the derivative instance to update
    /// </summary>
    public long Id { get; set; }

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
    /// Validator for DataStoreDerivativeUpdateCommand
    /// </summary>
    public class Validator : AbstractValidator<DataStoreDerivativeUpdateCommand>
    {
        private static readonly string[] ValidDerivativeTypes = ["ReadReplica", "Snapshot"];

        public Validator()
        {
            RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than 0.");

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
