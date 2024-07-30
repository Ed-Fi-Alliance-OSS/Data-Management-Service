// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

import { Client } from '@opensearch-project/opensearch';
import { QueryRequest, QueryResult, ResourceInfo } from '@edfi/meadowlark-core';
import { isDebugEnabled, Logger } from '@edfi/meadowlark-utilities';
import { handleOpenSearchError } from './OpenSearchException';

const moduleName = 'opensearch.repository.QueryOpensearch';

/**
 * Returns OpenSearch index name from the given ResourceInfo.
 *
 * OpenSearch indexes are required to be lowercase only, with no pound signs or periods.
 */
export function indexFromResourceInfo(resourceInfo: ResourceInfo): string {
  return `${resourceInfo.projectName}$${resourceInfo.resourceVersion}$${resourceInfo.resourceName}`
    .toLowerCase()
    .replace(/\./g, '-');
}

/**
 * DSL querying
 */
async function performDslQuery(client: Client, path: string, query: any, size: number, from: number): Promise<any> {
  return client.transport.request({
    method: 'POST',
    path: `/${path}/_search`,
    body: {
      from,
      query,
      size,
      sort: [{ _doc: { order: 'asc' } }],
    },
  });
}

/**
 * Entry point for querying with OpenSearch
 */
export async function queryDocuments(request: QueryRequest, client: Client): Promise<QueryResult> {
  const { resourceInfo, queryParameters, paginationParameters, traceId } = request;

  Logger.debug(`${moduleName}.queryDocuments Building query`, traceId);

  let documents: any = [];
  let recordCount: number;
  try {
    const matches: any[] = [];

    // API client requested filters
    if (Object.entries(queryParameters).length > 0) {
      Object.entries(queryParameters).forEach(([key, value]) => {
        matches.push({
          match_phrase: { [key]: value },
        });
      });
    }

    // Ownership-based security filter - if the resource is a descriptor we will ignore security
    if (request.security.authorizationStrategy.type === 'OWNERSHIP_BASED' && !resourceInfo.isDescriptor) {
      matches.push({
        match: {
          createdBy: request.security.clientId,
        },
      });
    }

    const query = {
      bool: {
        must: matches,
      },
    };

    if (isDebugEnabled()) {
      Logger.debug(`${moduleName}.queryDocuments queryDocuments executing query: ${JSON.stringify(query)}`, traceId);
    }
    const result = await performDslQuery(
      client,
      indexFromResourceInfo(resourceInfo),
      query,
      paginationParameters.limit as number,
      paginationParameters.offset as number,
    );

    recordCount = result.body.hits.total.value;

    if (recordCount > 0) {
      // eslint-disable-next-line no-underscore-dangle
      documents = result.body.hits.hits.map((datarow) => JSON.parse(datarow._source.info));
    }

    if (isDebugEnabled()) {
      const idsForLogging: string[] = documents.map((document) => document.id);
      Logger.debug(`${moduleName}.queryDocuments Ids of documents returned: ${JSON.stringify(idsForLogging)}`, traceId);
    }
  } catch (e) {
    return handleOpenSearchError(e, `${moduleName}.queryDocuments`, traceId);
  }

  return { response: 'QUERY_SUCCESS', documents, totalCount: recordCount };
}
