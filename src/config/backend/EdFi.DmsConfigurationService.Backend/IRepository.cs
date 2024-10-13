// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EdFi.DmsConfigurationService.Backend;

public interface IRepository<T> where T : class
{
    Task<GetResult<T>> GetAllAsync();
    Task<GetResult<T>> GetByIdAsync(long id);
    Task<InsertResult> AddAsync(T entity);
    Task<UpdateResult> UpdateAsync(T entity);
    Task<DeleteResult> DeleteAsync(long id);
}
