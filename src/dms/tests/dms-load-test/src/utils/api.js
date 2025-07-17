import http from 'k6/http';
import { check, group } from 'k6';
import { Rate } from 'k6/metrics';
import { dataStore } from './dataStore.js';

// Custom metrics
export const errorRate = new Rate('errors');

export class ApiClient {
    constructor(baseUrl, authManager) {
        this.baseUrl = baseUrl;
        this.authManager = authManager;
    }

    post(endpoint, data, resourceType, tags = {}) {
        const url = `${this.baseUrl}${endpoint}`;
        const headers = this.authManager.getAuthHeaders();
        
        const params = {
            headers: headers,
            tags: { ...tags, operation: 'POST', resourceType: resourceType }
        };

        const response = http.post(url, JSON.stringify(data), params);
        
        const success = check(response, {
            'POST status is 201': (r) => r.status === 201,
            'POST has location header': (r) => r.headers['Location'] !== undefined
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

        const response = http.del(url, params);
        
        const success = check(response, {
            'DELETE status is 204': (r) => r.status === 204
        });

        errorRate.add(!success);

        if (success) {
            return { success: true, response: response };
        } else {
            console.error(`DELETE ${endpoint} failed: ${response.status} - ${response.body}`);
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
    // Convert camelCase to kebab-case for URL
    const endpoint = resourceType.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase();
    return `/data/ed-fi/${endpoint}`;
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