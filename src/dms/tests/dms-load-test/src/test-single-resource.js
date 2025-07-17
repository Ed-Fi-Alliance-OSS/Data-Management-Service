import { SharedAuthManager } from './config/sharedAuth.js';
import { ApiClient, getResourceEndpoint } from './utils/api.js';
import { DataGenerator } from './generators/index.js';
import http from 'k6/http';

export const options = {
    iterations: 1,
    vus: 1,
};

export default function () {
    console.log('🧪 Testing single resource creation...');
    
    const apiBaseUrl = __ENV.API_BASE_URL || 'http://localhost:8080/api/data';
    const authManager = new SharedAuthManager({
        tokenUrl: __ENV.OAUTH_TOKEN_URL,
        clientId: __ENV.CLIENT_ID,
        clientSecret: __ENV.CLIENT_SECRET
    });
    
    // Get token and examine it
    const token = authManager.getToken();
    console.log(`🔑 Token obtained: ${token.substring(0, 50)}...`);
    console.log(`🔑 Token format check - dots found: ${(token.match(/\./g) || []).length}`);
    
    // Get auth headers
    const headers = authManager.getAuthHeaders();
    console.log(`📋 Headers:`, JSON.stringify(headers));
    
    // Create a simple descriptor
    const dataGenerator = new DataGenerator();
    const descriptorData = dataGenerator.generateForResourceType('gradeLevelDescriptors', 1);
    console.log(`📦 Generated data:`, JSON.stringify(descriptorData, null, 2));
    
    // Try to create it manually
    const endpoint = getResourceEndpoint('gradeLevelDescriptors');
    const url = `${apiBaseUrl}${endpoint}`;
    console.log(`🌐 POST URL: ${url}`);
    
    const response = http.post(url, JSON.stringify(descriptorData), {
        headers: headers
    });
    
    console.log(`📊 Response status: ${response.status}`);
    console.log(`📊 Response headers:`, JSON.stringify(response.headers));
    console.log(`📊 Response body: ${response.body}`);
    
    // Also try to get an existing resource to test auth
    console.log('\n🧪 Testing GET request...');
    const getResponse = http.get(url, { headers });
    console.log(`📊 GET Response status: ${getResponse.status}`);
    console.log(`📊 GET Response body preview: ${getResponse.body.substring(0, 200)}...`);
}