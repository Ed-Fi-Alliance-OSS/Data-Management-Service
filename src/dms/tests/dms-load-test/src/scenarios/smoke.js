import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedAuthManager } from '../config/sharedAuth.js';
import { DependencyResolver } from '../utils/dependencies.js';
import { ApiDataClient } from '../utils/apiDataClient.js';

// Test configuration
export const options = {
    vus: 1,
    duration: '5s',
    thresholds: {
        http_req_duration: ['p(95)<2000'], // 95% of requests must complete below 2s
        http_req_failed: ['rate<0.1'], // http errors should be less than 10%
        errors: ['rate<0.1'] // custom error rate should be less than 10%
    }
};

// Initialize test components
const apiBaseUrl = __ENV.API_BASE_URL || 'https://api.ed-fi.org/v7.3/api';

// Use shared auth manager to cache tokens across iterations
const sharedAuthManager = new SharedAuthManager({
    tokenUrl: __ENV.OAUTH_TOKEN_URL,
    clientId: __ENV.CLIENT_ID,
    clientSecret: __ENV.CLIENT_SECRET
});

export function setup() {
    console.log('🔧 Setting up smoke test...');

    // Use shared auth manager for setup to ensure token is cached
    const setupApiClient = new ApiDataClient(apiBaseUrl, sharedAuthManager);
    const setupDependencyResolver = new DependencyResolver(apiBaseUrl, sharedAuthManager);

    // Pre-warm the token cache
    try {
        const token = sharedAuthManager.getToken();
        console.log('✅ Token cached for all iterations');
    } catch (error) {
        console.error('❌ Initial token request failed:', error);
        throw error;
    }

    // Test dependency endpoint
    try {
        const dependencies = setupDependencyResolver.fetchDependencies();
        console.log(`✅ Dependencies endpoint working - found ${Object.keys(dependencies).length} resources`);
    } catch (error) {
        console.error('❌ Dependencies endpoint failed:', error);
        throw error;
    }

    // Test OpenAPI metadata endpoint
    const swaggerResponse = http.get(`${apiBaseUrl}/metadata/specifications/resources-spec.json`);
    check(swaggerResponse, {
        'OpenAPI metadata available': (r) => r.status === 200,
        'OpenAPI version present': (r) => r.json('openapi') !== undefined
    });

    if (swaggerResponse.status === 200) {
        console.log(`✅ OpenAPI metadata available - version ${swaggerResponse.json().openapi}`);
    } else {
        console.error('❌ OpenAPI metadata not available');
    }

    return {
        tokenCached: true,
        dependenciesAvailable: true
    };
}

export default function () {
    // Use shared auth manager for token caching across iterations
    const localApiClient = new ApiDataClient(apiBaseUrl, sharedAuthManager);

    // Log token info every 10th iteration
    if (__ITER % 10 === 0) {
        const tokenInfo = sharedAuthManager.getTokenInfo();
        console.log(`Iteration ${__ITER}: Token cached=${tokenInfo.hasToken}, expires=${tokenInfo.expiresAt}`);
    }

    // Test basic CRUD operations on a simple descriptor
    const descriptorData = {
        codeValue: `TEST${Date.now()}`,
        shortDescription: 'Test Descriptor',
        description: 'Test descriptor for smoke testing',
        namespace: 'uri://ed-fi.org/TestDescriptor'
    };

    // Create descriptor
    const createResult = localApiClient.post('/ed-fi/gradeLevelDescriptors', descriptorData, 'gradeLevelDescriptors');

    if (createResult.success) {
        console.log('✅ Successfully created test descriptor');

        // Read descriptor
        const location = createResult.response.headers['Location'];
        const id = location.split('/').pop();
        const readResult = localApiClient.get(`/ed-fi/gradeLevelDescriptors/${id}`);

        check(readResult.response, {
            'Can read created descriptor': (r) => r.status === 200,
            'Descriptor data matches': (r) => r.json('codeValue') === descriptorData.codeValue
        });

        // Update descriptor
        const updatedData = { ...descriptorData, id: id, shortDescription: 'Updated Test Descriptor' };
        const updateResult = localApiClient.put(`/ed-fi/gradeLevelDescriptors/${id}`, updatedData);

        check(updateResult.response, {
            'Can update descriptor': (r) => r.status === 204
        });

        // Delete descriptor
        const deleteResult = localApiClient.delete(`/ed-fi/gradeLevelDescriptors/${id}`);

        check(deleteResult.response, {
            'Can delete descriptor': (r) => r.status === 204
        });
    }

    // Test listing resources
    const listResult = localApiClient.get('/ed-fi/academicWeeks?limit=5');
    check(listResult.response, {
        'Can list resources': (r) => r.status === 200
    });

    // Test batch operations
    const batchRequests = [
        { method: 'GET', endpoint: '/ed-fi/academicWeeks?limit=1', tags: { name: 'batch_list' } },
        { method: 'GET', endpoint: '/ed-fi/courses?limit=1', tags: { name: 'batch_list' } }
    ];

    const batchResults = localApiClient.batch(batchRequests);
    const allSuccessful = batchResults.every(result => result.success);

    check(allSuccessful, {
        'Batch operations successful': (success) => success === true
    });

    sleep(1); // Avoid overwhelming the API
}

export function teardown(data) {
    console.log('🧹 Cleaning up smoke test...');
    console.log('✅ Smoke test completed');
}
