using System;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public static class JwtSigningKeyHelper
    {
        public static SecurityKey GenerateSigningKey(string? key = null)
        {
            key = key ?? Environment.GetEnvironmentVariable("JWT_SIGNING_KEY") ?? "YourSecretKeyForJWTWhichMustBeLongEnoughForSecurity123456789";
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException("JWT signing key is not configured.");
            var keyBytes = Encoding.UTF8.GetBytes(key);
            if (keyBytes.Length < 32)
                throw new InvalidOperationException($"JWT signing key must be at least 32 bytes long. Current length: {keyBytes.Length}");
            return new SymmetricSecurityKey(keyBytes);
        }
    }
}
