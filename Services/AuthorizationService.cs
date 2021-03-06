using Codesanook.Authorization.Handlers;
using Codesanook.Authorization.Models;
using Codesanook.Common.DataType;
using Codesanook.Common.DataTypes;
using Jose;
using Orchard.ContentManagement;
using Orchard.Localization;
using Orchard.Logging;
using Orchard.Mvc;
using Orchard.Roles.Models;
using Orchard.Security;
using Orchard.Security.Permissions;
using Orchard.Settings;
using Orchard.Users.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace Codesanook.Authorization.Services
{
    //https://scotch.io/tutorials/the-anatomy-of-a-json-web-token
    //https://github.com/dvsekhvalnov/jose-jwt
    //http://www.svlada.com/jwt-token-authentication-with-spring-boot/
    public class AuthorizationService : IAuthorizationService
    {
        private static Regex accessTokenRegex = new Regex(@"Bearer\s+(?<accessToken>.+)", RegexOptions.Compiled);

        private readonly IContentManager contentManager;
        private readonly ISiteService siteService;
        private readonly IAuthorizationEventHandler authorizationEventHandler;
        private readonly IMembershipService membershipService;
        private readonly Orchard.Security.IAuthorizationService orchardAuthorizationService;
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly AuthorizationSettingPart authorizationSettingPart;
        private readonly byte[] refreshTokenSecretKey;
        private readonly byte[] accessTokenSecretKey;

        public AuthorizationService(
            IContentManager contentManager,
            IMembershipService membershipService,
            Orchard.Security.IAuthorizationService authorizationService,
            IHttpContextAccessor httpContextAccessor,
            ISiteService siteService,
            IAuthorizationEventHandler authorizationEventHandler
        )
        {
            this.contentManager = contentManager;
            this.membershipService = membershipService;
            this.orchardAuthorizationService = authorizationService;
            this.httpContextAccessor = httpContextAccessor;
            this.siteService = siteService;
            this.authorizationEventHandler = authorizationEventHandler;
            authorizationSettingPart = this.siteService.GetSiteSettings().As<AuthorizationSettingPart>();
            refreshTokenSecretKey = authorizationSettingPart.RefreshTokenSecretKey.GetBytesFromAsciiString();
            accessTokenSecretKey = authorizationSettingPart.AccessTokenSecretKey.GetBytesFromAsciiString();
        }

        //property injection
        public ILogger Logger { get; set; }
        public Localizer T { get; set; }

        public IUser GetAuthenticatedUser()
        {
            var accessToken = GetAccessTokenFromHeaderRequest();
            var accessTokenClaim = GetValidClaim(accessToken, accessTokenSecretKey);
            var user = GetValidUser(accessTokenClaim);
            return user;
        }

        public void CheckAccess(Permission permission, IUser user, IContent content = null)
        {
            orchardAuthorizationService.CheckAccess(permission, user, content);
        }

        public TokenResponse CreateRefreshTokenResponse(RefreshTokenRequest request)
        {
            var user = ValidateUser(request.Email, request.Password);
            if (user == null)
            {
                throw new AuthenticationException("email or password is incorrect");
            }
            return CreateTokenResponse(user);
        }

        public TokenResponse CreateAccessTokenResponse(AccessTokenRequest request)
        {
            var refreshTokenClaim = GetValidClaim(request.RefreshToken, refreshTokenSecretKey);
            var user = GetValidUser(refreshTokenClaim);
            var authorizationPart = user.As<AuthorizationPart>();
            if (string.Compare(refreshTokenClaim.jti, authorizationPart.RefreshTokenId.ToString(), StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new AuthenticationException("A refresh token id does not match, user may revoke a refresh token");
            }

            return CreateTokenResponse(user);
        }

        private TokenResponse CreateTokenResponse(IUser user)
        {
            var refreshToken = CreateRefreshToken(user);
            var accessToken = CreateAccessToken(user);
            var response = new TokenResponse()
            {
                RefreshToken = refreshToken,
                AccessToken = accessToken,
                UserId = user.Id
            };
            return response;
        }

        private string CreateRefreshToken(IUser user)
        {
            var now = DateTime.UtcNow;
            var refreshTokenExpiration = now.AddDays(authorizationSettingPart.RefreshTokenExpireInDays)
               .GetUtcTimestamp();
            var refreshTokenId = Guid.NewGuid();
            var refreshTokenClaim = new Claim()
            {
                sub = user.Email,
                scopes = new[] { Claim.ROLE_REFRESH_TOKEN },
                exp = refreshTokenExpiration,
                jti = refreshTokenId.ToString()
            };

            var authorizationPart = user.As<AuthorizationPart>();
            authorizationPart.RefreshTokenId = refreshTokenId;
            return EncryptedClaim(refreshTokenClaim, refreshTokenSecretKey);
        }

        private string CreateAccessToken(IUser user)
        {
            var now = DateTime.UtcNow;
            var accessTokenExpiration = now.AddMinutes(authorizationSettingPart.AccessTokenExpireInMinutes)
                    .GetUtcTimestamp();
            var role = user.As<UserRolesPart>();
            var accessTokenClaim = new Claim()
            {
                sub = user.Email,
                scopes = role.Roles.ToArray(),//role of the current user
                jti = Guid.NewGuid().ToString(),
                exp = accessTokenExpiration
            };

            return EncryptedClaim(accessTokenClaim, accessTokenSecretKey);
        }

        private string GetAccessTokenFromHeaderRequest()
        {
            var httpContext = httpContextAccessor.Current();
            var request = httpContext.Request;
            const string headerKey = "Authorization";
            if (!request.Headers.AllKeys.Contains(headerKey))
            {
                throw new AuthenticationException("no access token");
            }

            var rawValue = request.Headers.GetValues(headerKey).First();
            var match = accessTokenRegex.Match(rawValue);
            var accessToken = match.Groups["accessToken"].Value;
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new AuthenticationException("no access token");
            }
            return accessToken;
        }

        private Claim GetValidClaim(string token, byte[] secretKey)
        {
            try
            {
                var claim = JWT.Decode<Claim>(token,
                    secretKey,
                    JweAlgorithm.A256GCMKW,
                    JweEncryption.A256CBC_HS512);

                var utcNow = DateTime.UtcNow;
                var expire = claim.exp.GetUtcDateTime();
                if (utcNow > expire)
                {
                    throw new AuthenticationException("token expire");
                }

                return claim;
            }
            catch (Exception ex) when (!(ex is AuthenticationException))
            {
                //Log error and generalize everything to invalid token
                Logger.Error(ex, ex.Message);
                throw new AuthenticationException("invalid token");
            }
        }

        private string EncryptedClaim(Claim claim, byte[] secretKey)
        {
            var token = JWT.Encode(
                claim,
                secretKey,
                JweAlgorithm.A256GCMKW,
                JweEncryption.A256CBC_HS512);
            return token;
        }

        public IUser ValidateUser(string email, string password)
        {
            if (string.IsNullOrEmpty(email))
            {
                throw new AuthenticationException($"validate user error because email is null or empty");
            }

            var user = GetValidUser(email);

            List<LocalizedString> validationErrors = null;
            return membershipService.ValidateUser(email, password, out validationErrors);
        }

        private IUser GetValidUser(Claim claim)
        {
            return GetValidUser(claim.sub);
        }

        private IUser GetValidUser(string email)
        {
            var lowerEmail = email.ToLower();
            var user = contentManager.Query<UserPart, UserPartRecord>()
                .Where<UserPartRecord>(u => u.Email == lowerEmail)
                .List()
                .SingleOrDefault();

            if (user == null)
            {
                throw new AuthenticationException($"no user with email {lowerEmail}");
            }

            if (user.EmailStatus != UserStatus.Approved)
            {
                var exception = new AuthenticationException(
                    string.Format(authorizationSettingPart.UnverifiedEmailErrorMessageTemplate, lowerEmail)
                );
                authorizationEventHandler.OnUnverifiedEmailException(exception, user);
                throw exception;
            }

            if (user.RegistrationStatus != UserStatus.Approved)
            {
                var exception = new AuthenticationException(
                    string.Format(authorizationSettingPart.UnactivatedErrorMesageTemplate, lowerEmail)
                );
                authorizationEventHandler.OnUnactivatedException(exception, user);
                throw exception;
            }

            return user;
        }
    }
}