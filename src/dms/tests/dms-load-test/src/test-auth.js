import { SharedAuthManager } from './config/sharedAuth.js';
import http from 'k6/http';
import { check } from 'k6';

export const options = {
    iterations: 1,
    vus: 1,
};

export default function () {
    console.log('🔐 Testing authentication...');
    console.log(`📍 OAuth Token URL: ${__ENV.OAUTH_TOKEN_URL}`);
    console.log(`🔑 Client ID: ${__ENV.CLIENT_ID}`);
    console.log(`🔒 Client Secret: ${__ENV.CLIENT_SECRET ? '[REDACTED]' : '[NOT SET]'}`);
    
    try {
        // Test direct OAuth request
        console.log('\n📝 Testing direct OAuth token request...');
        const payload = {
            grant_type: 'client_credentials',
            client_id: __ENV.CLIENT_ID,
            client_secret: __ENV.CLIENT_SECRET,
        };
        
        const params = {
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded',
            },
        };
        
        const response = http.post(__ENV.OAUTH_TOKEN_URL, payload, params);
        
        console.log(`📊 Response status: ${response.status}`);
        console.log(`📊 Response body: ${response.body}`);
        
        const success = check(response, {
            'OAuth token request successful': (r) => r.status === 200,
            'Response contains access_token': (r) => r.json('access_token') !== null,
        });
        
        if (success) {
            const token = response.json('access_token');
            console.log(`✅ Token obtained successfully (${token.substring(0, 20)}...)`);
            
            // Test API request with token
            console.log('\n📝 Testing API request with token...');
            const apiBaseUrl = __ENV.API_BASE_URL || 'https://api.ed-fi.org/v7.3/api';
            const apiResponse = http.get(`${apiBaseUrl}/metadata/dependencies`, {
                headers: {
                    'Authorization': `Bearer ${token}`,
                    'Accept': 'application/json'
                }
            });
            
            console.log(`📊 API Response status: ${apiResponse.status}`);
            console.log(`📊 API Response body preview: ${apiResponse.body.substring(0, 200)}...`);
            
            check(apiResponse, {
                'API request successful': (r) => r.status === 200,
                'Response is JSON': (r) => r.headers['Content-Type'] && r.headers['Content-Type'].includes('application/json'),
            });
            
            // Test SharedAuthManager
            console.log('\n📝 Testing SharedAuthManager...');
            const authManager = new SharedAuthManager({
                tokenUrl: __ENV.OAUTH_TOKEN_URL,
                clientId: __ENV.CLIENT_ID,
                clientSecret: __ENV.CLIENT_SECRET
            });
            
            const sharedToken = authManager.getToken();
            console.log(`✅ SharedAuthManager token obtained: ${sharedToken ? sharedToken.substring(0, 20) + '...' : 'NULL'}`);
            
        } else {
            console.error('❌ Failed to obtain OAuth token');
            console.error('Response:', response.body);
        }
        
    } catch (error) {
        console.error('❌ Authentication test failed:', error.message);
        console.error('Stack trace:', error.stack);
    }
}