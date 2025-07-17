import http from 'k6/http';
import { check } from 'k6';
import encoding from 'k6/encoding';

// Global token storage - shared across all VUs and iterations
const tokenStore = {
    token: null,
    tokenExpiry: null,
    isRefreshing: false
};

export class SharedAuthManager {
    constructor(config) {
        this.tokenUrl = config.tokenUrl || __ENV.OAUTH_TOKEN_URL;
        this.clientId = config.clientId || __ENV.CLIENT_ID;
        this.clientSecret = config.clientSecret || __ENV.CLIENT_SECRET;
    }

    getToken() {
        // Check if we have a valid token in the global store
        if (tokenStore.token && tokenStore.tokenExpiry && Date.now() < tokenStore.tokenExpiry) {
            return tokenStore.token;
        }

        // Prevent multiple VUs from refreshing at the same time
        if (tokenStore.isRefreshing) {
            // Wait a bit and try again
            const maxRetries = 10;
            for (let i = 0; i < maxRetries; i++) {
                if (!tokenStore.isRefreshing && tokenStore.token) {
                    return tokenStore.token;
                }
                // Sleep for 100ms
                const waitTime = 0.1;
                const start = Date.now();
                while ((Date.now() - start) < waitTime * 1000) {
                    // Busy wait
                }
            }
        }

        // Mark as refreshing
        tokenStore.isRefreshing = true;

        try {
            // Request new token
            const basicAuth = encoding.b64encode(`${this.clientId}:${this.clientSecret}`);
            const params = {
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'Authorization': `Basic ${basicAuth}`,
                },
            };

            const payload = 'grant_type=client_credentials&scope=E2E-NoFurtherAuthRequiredClaimSet';

            console.log(`Requesting new token from: ${this.tokenUrl}`);
            const response = http.post(this.tokenUrl, payload, params);

            const success = check(response, {
                'shared token request successful': (r) => r.status === 200,
                'shared token received': (r) => r.json('access_token') !== undefined,
            });

            if (!success) {
                console.error(`Token request failed: ${response.status} - ${response.body}`);
                
                // Check if it's a rate limit error
                if (response.status === 429) {
                    const retryAfter = response.headers['Retry-After'] || '300';
                    console.error(`Rate limited! Server suggests waiting ${retryAfter} seconds.`);
                    console.error(`Consider waiting before running tests or reusing existing tokens.`);
                }
                
                throw new Error('Failed to obtain access token');
            }

            const data = response.json();
            tokenStore.token = data.access_token;
            
            // Set token expiry with 5 minute buffer
            const expiresIn = data.expires_in || 3600;
            tokenStore.tokenExpiry = Date.now() + (expiresIn - 300) * 1000;

            console.log(`Token obtained and cached, expires in ${expiresIn} seconds`);
            return tokenStore.token;
        } finally {
            tokenStore.isRefreshing = false;
        }
    }

    getAuthHeaders() {
        return {
            'Authorization': `Bearer ${this.getToken()}`,
            'Content-Type': 'application/json',
        };
    }

    invalidateToken() {
        tokenStore.token = null;
        tokenStore.tokenExpiry = null;
    }

    // Get token info for debugging
    getTokenInfo() {
        return {
            hasToken: !!tokenStore.token,
            expiresAt: tokenStore.tokenExpiry ? new Date(tokenStore.tokenExpiry).toISOString() : null,
            isExpired: tokenStore.tokenExpiry ? Date.now() >= tokenStore.tokenExpiry : true
        };
    }
}