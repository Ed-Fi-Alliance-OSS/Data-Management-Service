import http from 'k6/http';
import { check } from 'k6';

export class AuthManager {
    constructor(config) {
        this.tokenUrl = config.tokenUrl || __ENV.OAUTH_TOKEN_URL;
        this.clientId = config.clientId || __ENV.CLIENT_ID;
        this.clientSecret = config.clientSecret || __ENV.CLIENT_SECRET;
        this.token = null;
        this.tokenExpiry = null;
    }

    getToken() {
        // Check if we have a valid token
        if (this.token && this.tokenExpiry && Date.now() < this.tokenExpiry) {
            return this.token;
        }

        // Request new token
        const params = {
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded',
            },
        };

        const payload = {
            grant_type: 'client_credentials',
            client_id: this.clientId,
            client_secret: this.clientSecret,
        };

        console.log(`Requesting token from: ${this.tokenUrl}`);
        const response = http.post(this.tokenUrl, payload, params);

        const success = check(response, {
            'token request successful': (r) => r.status === 200,
            'token received': (r) => r.json('access_token') !== undefined,
        });

        if (!success) {
            console.error(`Token request failed: ${response.status} - ${response.body}`);
            throw new Error('Failed to obtain access token');
        }

        const data = response.json();
        this.token = data.access_token;
        
        // Set token expiry with 5 minute buffer
        const expiresIn = data.expires_in || 3600;
        this.tokenExpiry = Date.now() + (expiresIn - 300) * 1000;

        console.log(`Token obtained successfully, expires in ${expiresIn} seconds`);
        return this.token;
    }

    getAuthHeaders() {
        return {
            'Authorization': `Bearer ${this.getToken()}`,
            'Content-Type': 'application/json',
        };
    }

    invalidateToken() {
        this.token = null;
        this.tokenExpiry = null;
    }
}