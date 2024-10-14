// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// Generic repository for basic CRUD operations
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>
    /// Gets all entities of type T
    /// </summary>
    /// <returns>
    /// GetResult.GetSuccess will hold the results
    /// GetResult.UnknownFailure on caught exception
    /// </returns>
    Task<GetResult<T>> GetAllAsync();
    /// <summary>
    /// Get single entity of type T by id
    /// </summary>
    /// <param name="id">The database identity</param>
    /// <returns>
    /// GetResult.GetSuccess will hold the result
    /// GetResult.UnknownFailure on caught exception
    /// </returns>
    Task<GetResult<T>> GetByIdAsync(long id);
    /// <summary>
    /// Add entity of type T
    /// </summary>
    /// <param name="entity">Entity to add</param>
    /// <returns>
    /// UpdateResult.UpdateSuccess will hold the Id of the newly created entity
    /// UpdateResult.UnknownFailure on caught exception
    /// </returns>
    Task<InsertResult> AddAsync(T entity);
    /// <summary>
    /// Update entity of type T
    /// </summary>
    /// <param name="entity">Entity to update</param>
    /// <returns>
    /// UpdateResult.UpdateSuccess will hold the number of entities updated
    /// UpdateResult.UpdateFailureNotExists when entity with this identity not present in database.
    /// UpdateResult.UnknownFailure on caught exception
    /// </returns>
    Task<UpdateResult> UpdateAsync(T entity);
    /// <summary>
    /// Delete entity of type T
    /// </summary>
    /// <param name="id">Id of entity to delete</param>
    /// <returns>
    /// DeleteResult.DeleteSuccess will hold the number of entities deleted
    /// DeleteResult.DeleteFailureNotExists when entity with this identity not present in database.
    /// DeleteResult.UnknownFailure on caught exception
    /// </returns>
    Task<DeleteResult> DeleteAsync(long id);
}
