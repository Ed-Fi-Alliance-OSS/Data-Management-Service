// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Handler;

internal sealed class TokenInfoRelationalMappingSetResolver(
    ResolveMappingSetMiddleware resolveMappingSetMiddleware
) : ITokenInfoRelationalMappingSetResolver
{
    public async Task<TokenInfoRelationalMappingSetResolutionResult> ResolveAsync(RequestInfo requestInfo)
    {
        if (requestInfo.MappingSet is not null)
        {
            return new TokenInfoRelationalMappingSetResolutionResult(true, requestInfo.MappingSet);
        }

        await resolveMappingSetMiddleware.Execute(requestInfo, () => Task.CompletedTask);

        return new TokenInfoRelationalMappingSetResolutionResult(
            requestInfo.MappingSet is not null,
            requestInfo.MappingSet
        );
    }
}
