import http from 'k6/http';
import { check, group } from 'k6';
import { Rate } from 'k6/metrics';
import exec from 'k6/execution';
import { dataStore } from './dataStore.js';

// Custom metrics
export const errorRate = new Rate('errors');

export class ApiDataClient {
    constructor(baseUrl, authManager) {
        this.baseUrl = `${baseUrl}/data`;
        this.authManager = authManager;
    }

    post(endpoint, data, resourceType, tags = {}) {
        const url = `${this.baseUrl}${endpoint}`;
        const headers = this.authManager.getAuthHeaders();

        // Validate data is an object and not a string
        if (typeof data !== 'object' || data === null) {
            console.error(`ERROR: Invalid data type for ${resourceType}. Expected object, got ${typeof data}: ${data}`);
            return { success: false, error: `Invalid data type: ${typeof data}` };
        }

        // Remove any 'id' field from the data before sending
        const cleanData = { ...data };
        if (cleanData.id) {
            console.warn(`WARNING: Removing 'id' field from ${resourceType} data before POST`);
            delete cleanData.id;
        }

        const params = {
            headers: headers,
            tags: { ...tags, operation: 'POST', resourceType: resourceType }
        };

        const requestBody = JSON.stringify(cleanData);
        const response = http.post(url, requestBody, params);

        if (__ENV.DEBUG === 'true' && response.status === 400) {
            console.log(`DEBUG: Request body that caused 400 error: ${requestBody}`);
            console.log(`DEBUG: Response body: ${response.body}`);
        }

        const success = check(response, {
            'POST status is 201 or 200': (r) => r.status === 201 || r.status === 200,
            'POST has location header or already exists': (r) => r.headers['Location'] !== undefined || r.status === 200
        });

        errorRate.add(!success);

        if (success) {
            // Extract ID from location header
            const location = response.headers['Location'];
            const id = location ? location.split('/').pop() : null;

            // Store the created resource
            const createdResource = { ...data, id: id };
            dataStore.addResource(resourceType, createdResource);

            return { success: true, data: createdResource, response: response };
        } else {
            console.error(`POST ${endpoint} failed: ${response.status} - ${response.body}`);
            
            // Check for 4xx errors and abort if configured
            if (response.status >= 400 && response.status < 500 && __ENV.ABORT_ON_4XX === 'true') {
                console.error(`CRITICAL: ${response.status} error detected. Test will be aborted.`);
                console.error(`Endpoint: ${endpoint}`);
                console.error(`Resource Type: ${resourceType}`);
                console.error(`Response: ${response.body}`);
                console.error(`Request Body: ${requestBody}`);
                
                exec.test.abort(`Test aborted due to ${response.status} error on POST ${endpoint}: ${response.body}`);
            }
            
            return { success: false, error: response.body, response: response };
        }
    }

    get(endpoint, tags = {}) {
        const url = `${this.baseUrl}${endpoint}`;
        const headers = this.authManager.getAuthHeaders();

        const params = {
            headers: headers,
            tags: { ...tags, operation: 'GET' }
        };

        const response = http.get(url, params);

        const success = check(response, {
            'GET status is 200': (r) => r.status === 200
        });

        errorRate.add(!success);

        if (success) {
            return { success: true, data: response.json(), response: response };
        } else {
            console.error(`GET ${endpoint} failed: ${response.status} - ${response.body}`);
            
            // Check for 4xx errors and abort if configured
            if (response.status >= 400 && response.status < 500 && __ENV.ABORT_ON_4XX === 'true') {
                console.error(`CRITICAL: ${response.status} error detected. Test will be aborted.`);
                console.error(`Endpoint: ${endpoint}`);
                console.error(`Response: ${response.body}`);
                
                exec.test.abort(`Test aborted due to ${response.status} error on GET ${endpoint}: ${response.body}`);
            }
            
            return { success: false, error: response.body, response: response };
        }
    }

    put(endpoint, data, tags = {}) {
        const url = `${this.baseUrl}${endpoint}`;
        const headers = this.authManager.getAuthHeaders();

        const params = {
            headers: headers,
            tags: { ...tags, operation: 'PUT' }
        };

        const response = http.put(url, JSON.stringify(data), params);

        const success = check(response, {
            'PUT status is 200 or 204': (r) => r.status === 200 || r.status === 204
        });

        errorRate.add(!success);

        if (success) {
            return { success: true, response: response };
        } else {
            console.error(`PUT ${endpoint} failed: ${response.status} - ${response.body}`);
            
            // Check for 4xx errors and abort if configured
            if (response.status >= 400 && response.status < 500 && __ENV.ABORT_ON_4XX === 'true') {
                console.error(`CRITICAL: ${response.status} error detected. Test will be aborted.`);
                console.error(`Endpoint: ${endpoint}`);
                console.error(`Response: ${response.body}`);
                console.error(`Request Body: ${JSON.stringify(data)}`);
                
                exec.test.abort(`Test aborted due to ${response.status} error on PUT ${endpoint}: ${response.body}`);
            }
            
            return { success: false, error: response.body, response: response };
        }
    }

    delete(endpoint, tags = {}) {
        const url = `${this.baseUrl}${endpoint}`;
        const headers = this.authManager.getAuthHeaders();

        const params = {
            headers: headers,
            tags: { ...tags, operation: 'DELETE' }
        };

        const response = http.del(url, null, params);

        const success = check(response, {
            'DELETE status is 204': (r) => r.status === 204
        });

        errorRate.add(!success);

        if (success) {
            return { success: true, response: response };
        } else {
            console.error(`DELETE ${endpoint} failed: ${response.status} - ${response.body}`);
            
            // Check for 4xx errors and abort if configured
            if (response.status >= 400 && response.status < 500 && __ENV.ABORT_ON_4XX === 'true') {
                console.error(`CRITICAL: ${response.status} error detected. Test will be aborted.`);
                console.error(`Endpoint: ${endpoint}`);
                console.error(`Response: ${response.body}`);
                
                exec.test.abort(`Test aborted due to ${response.status} error on DELETE ${endpoint}: ${response.body}`);
            }
            
            return { success: false, error: response.body, response: response };
        }
    }

    // Batch operations
    batch(requests) {
        const headers = this.authManager.getAuthHeaders();

        const batchRequests = requests.map(req => {
            const url = `${this.baseUrl}${req.endpoint}`;
            const params = {
                headers: headers,
                tags: req.tags || {}
            };

            switch (req.method.toUpperCase()) {
                case 'GET':
                    return ['GET', url, null, params];
                case 'POST':
                    return ['POST', url, JSON.stringify(req.data), params];
                case 'PUT':
                    return ['PUT', url, JSON.stringify(req.data), params];
                case 'DELETE':
                    return ['DELETE', url, null, params];
                default:
                    throw new Error(`Unsupported method: ${req.method}`);
            }
        });

        const responses = http.batch(batchRequests);

        return responses.map((response, index) => {
            const request = requests[index];
            const success = this.checkResponseStatus(response, request.method);

            errorRate.add(!success);

            return {
                request: request,
                response: response,
                success: success
            };
        });
    }

    checkResponseStatus(response, method) {
        switch (method.toUpperCase()) {
            case 'GET':
                return response.status === 200;
            case 'POST':
                return response.status === 201;
            case 'PUT':
                return response.status === 200 || response.status === 204;
            case 'DELETE':
                return response.status === 204;
            default:
                return false;
        }
    }
}

// Helper function to create resource endpoints
export function getResourceEndpoint(resourceType) {
    // The resource type from dependencies has already been stripped of /ed-fi/
    // so we need to add it back
    return `/ed-fi/${resourceType}`;
}

// Helper function to handle rate limiting
export function handleRateLimit(response) {
    if (response.status === 429) {
        const retryAfter = response.headers['Retry-After'] || '1';
        console.log(`Rate limited. Waiting ${retryAfter} seconds...`);
        sleep(parseInt(retryAfter));
        return true;
    }
    return false;
}
