// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public abstract class PagingQueryValidator<T> : AbstractValidator<T>
    where T : PagingQuery
{
    private static readonly HashSet<string> ValidDirections = new(StringComparer.OrdinalIgnoreCase)
    {
        "asc",
        "ascending",
        "desc",
        "descending",
    };

    protected PagingQueryValidator(IReadOnlySet<string> allowedOrderByFields)
    {
        // Deterministic, value-free allowed-field list: sorted ordinal-ignore-case so the message does not
        // depend on HashSet enumeration order, and never echoes the supplied 'orderBy' value.
        string allowedOrderByList = string.Join(
            ", ",
            allowedOrderByFields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        );

        RuleFor(q => q.Offset)
            .GreaterThanOrEqualTo(0)
            .When(q => q.Offset.HasValue)
            .WithMessage("'offset' must be greater than or equal to 0.");

        RuleFor(q => q.Limit)
            .GreaterThan(0)
            .When(q => q.Limit.HasValue)
            .WithMessage("'limit' must be greater than 0.");

        RuleFor(q => q.Direction)
            .Must(d => d is null || ValidDirections.Contains(d))
            .WithMessage("The direction query parameter must be one of: asc, ascending, desc, descending.");

        RuleFor(q => q.OrderBy)
            .Must(ob => ob is null || allowedOrderByFields.Contains(ob))
            .WithMessage($"'orderBy' is not a valid field. Allowed values: {allowedOrderByList}.");
    }
}

public class VendorPagingQueryValidator : PagingQueryValidator<FrontendVendorQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "company",
        "contactName",
        "contactEmailAddress",
    };

    public VendorPagingQueryValidator()
        : base(AllowedFields) { }
}

public class ApplicationPagingQueryValidator : PagingQueryValidator<FrontendApplicationQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "applicationName",
        "vendorId",
        "claimSetName",
    };

    public ApplicationPagingQueryValidator()
        : base(AllowedFields)
    {
        RuleFor(q => q)
            .Must(q => !(q.Id.HasValue && !string.IsNullOrEmpty(q.Ids)))
            .WithMessage("'id' and 'ids' cannot be used together.");

        RuleFor(q => q.Ids)
            .Must(ids =>
            {
                if (string.IsNullOrEmpty(ids))
                {
                    return true;
                }
                return Array.TrueForAll(
                    ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    segment => int.TryParse(segment, out _)
                );
            })
            .WithMessage("The 'ids' query parameter must be a comma-separated list of integers.");
    }
}

public class ApiClientPagingQueryValidator : PagingQueryValidator<FrontendApiClientQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "applicationId",
        "name",
    };

    public ApiClientPagingQueryValidator()
        : base(AllowedFields) { }
}

public class DataStorePagingQueryValidator : PagingQueryValidator<FrontendDataStoreQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "dataStoreType",
        "name",
    };

    public DataStorePagingQueryValidator()
        : base(AllowedFields) { }
}

public class ClaimSetPagingQueryValidator : PagingQueryValidator<FrontendClaimSetQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "name",
        "claimSetName",
    };

    public ClaimSetPagingQueryValidator()
        : base(AllowedFields) { }
}

public class TenantPagingQueryValidator : PagingQueryValidator<FrontendPagingQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "name",
    };

    public TenantPagingQueryValidator()
        : base(AllowedFields) { }
}

public class DataStoreDerivativePagingQueryValidator : PagingQueryValidator<FrontendPagingQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "dataStoreId",
        "derivativeType",
    };

    public DataStoreDerivativePagingQueryValidator()
        : base(AllowedFields) { }
}

public class DataStoreContextPagingQueryValidator : PagingQueryValidator<FrontendPagingQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "dataStoreId",
        "contextKey",
        "contextValue",
    };

    public DataStoreContextPagingQueryValidator()
        : base(AllowedFields) { }
}

public class ProfilePagingQueryValidator : PagingQueryValidator<FrontendProfileQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "name",
    };

    public ProfilePagingQueryValidator()
        : base(AllowedFields) { }
}

public class ResourceClaimPagingQueryValidator : PagingQueryValidator<FrontendResourceClaimQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "name",
        "parentId",
        "parentName",
    };

    public ResourceClaimPagingQueryValidator()
        : base(AllowedFields) { }
}

public class ResourceClaimActionPagingQueryValidator : PagingQueryValidator<FrontendResourceClaimActionQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "resourceClaimId",
        "resourceName",
    };

    public ResourceClaimActionPagingQueryValidator()
        : base(AllowedFields) { }
}

public class ResourceClaimActionAuthStrategyPagingQueryValidator
    : PagingQueryValidator<FrontendResourceClaimActionAuthStrategyQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "resourceClaimId",
        "resourceName",
        "claimName",
    };

    public ResourceClaimActionAuthStrategyPagingQueryValidator()
        : base(AllowedFields) { }
}
