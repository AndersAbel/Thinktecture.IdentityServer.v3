﻿/*
 * Copyright 2014, 2015 Dominick Baier, Brock Allen
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Thinktecture.IdentityModel;
using Thinktecture.IdentityServer.Core.Extensions;
using Thinktecture.IdentityServer.Core.Logging;
using Thinktecture.IdentityServer.Core.Models;
using Thinktecture.IdentityServer.Core.Services;
using Thinktecture.IdentityServer.Core.Validation;

#pragma warning disable 1591

namespace Thinktecture.IdentityServer.Core.ResponseHandling
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class AuthorizeResponseGenerator
    {
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private readonly ITokenService _tokenService;
        private readonly IAuthorizationCodeStore _authorizationCodes;
        private readonly IEventService _events;

        public AuthorizeResponseGenerator(ITokenService tokenService, IAuthorizationCodeStore authorizationCodes, IEventService events)
        {
            _tokenService = tokenService;
            _authorizationCodes = authorizationCodes;
            _events = events;
        }

        public async Task<AuthorizeResponse> CreateResponseAsync(ValidatedAuthorizeRequest request)
        {
            if (request.Flow == Flows.AuthorizationCode)
            {
                return await CreateCodeFlowResponseAsync(request);
            }
            if (request.Flow == Flows.Implicit)
            {
                return await CreateImplicitFlowResponseAsync(request);
            }
            if (request.Flow == Flows.Hybrid)
            {
                return await CreateHybridFlowResponseAsync(request);
            }

            Logger.Error("Unsupported flow: " + request.Flow.ToString());
            throw new InvalidOperationException("invalid flow: " + request.Flow.ToString());
        }

        private async Task<AuthorizeResponse> CreateHybridFlowResponseAsync(ValidatedAuthorizeRequest request)
        {
            var code = await CreateCodeAsync(request);
            var response = await CreateImplicitFlowResponseAsync(request, code);
            response.Code = code;

            return response;
        }

        public async Task<AuthorizeResponse> CreateCodeFlowResponseAsync(ValidatedAuthorizeRequest request)
        {
            var code = await CreateCodeAsync(request);

            var response = new AuthorizeResponse
            {
                Request = request,
                RedirectUri = request.RedirectUri,
                Code = code,
                State = request.State
            };

            if (request.IsOpenIdRequest)
            {
                response.SessionState = GenerateSessionStateValue(request);
            }

            return response;
        }

        private async Task<string> CreateCodeAsync(ValidatedAuthorizeRequest request)
        {
            var code = new AuthorizationCode
            {
                Client = request.Client,
                Subject = request.Subject,

                IsOpenId = request.IsOpenIdRequest,
                RequestedScopes = request.ValidatedScopes.GrantedScopes,
                RedirectUri = request.RedirectUri,
                Nonce = request.Nonce,

                WasConsentShown = request.WasConsentShown,
            };

            // store id token and access token and return authorization code
            var id = CryptoRandom.CreateUniqueId();
            await _authorizationCodes.StoreAsync(id, code);

            RaiseCodeIssuedEvent(id, code);

            return id;
        }

        public async Task<AuthorizeResponse> CreateImplicitFlowResponseAsync(ValidatedAuthorizeRequest request, string authorizationCode = null)
        {
            string accessTokenValue = null;
            int accessTokenLifetime = 0;

            var responseTypes = request.ResponseType.FromSpaceSeparatedString();

            if (responseTypes.Contains(Constants.ResponseTypes.Token))
            {
                var tokenRequest = new TokenCreationRequest
                {
                    Subject = request.Subject,
                    Client = request.Client,
                    Scopes = request.ValidatedScopes.GrantedScopes,

                    ValidatedRequest = request
                };

                var accessToken = await _tokenService.CreateAccessTokenAsync(tokenRequest);
                accessTokenLifetime = accessToken.Lifetime;

                accessTokenValue = await _tokenService.CreateSecurityTokenAsync(accessToken);
            }

            string jwt = null;
            if (responseTypes.Contains(Constants.ResponseTypes.IdToken))
            {
                var tokenRequest = new TokenCreationRequest
                {
                    ValidatedRequest = request,
                    Subject = request.Subject,
                    Client = request.Client,
                    Scopes = request.ValidatedScopes.GrantedScopes,

                    Nonce = request.Raw.Get(Constants.AuthorizeRequest.Nonce),
                    IncludeAllIdentityClaims = !request.AccessTokenRequested,
                    AccessTokenToHash = accessTokenValue,
                    AuthorizationCodeToHash = authorizationCode
                };

                var idToken = await _tokenService.CreateIdentityTokenAsync(tokenRequest);
                jwt = await _tokenService.CreateSecurityTokenAsync(idToken);
            }

            var response = new AuthorizeResponse
            {
                Request = request,
                RedirectUri = request.RedirectUri,
                AccessToken = accessTokenValue,
                AccessTokenLifetime = accessTokenLifetime,
                IdentityToken = jwt,
                State = request.State,
                Scope = request.ValidatedScopes.GrantedScopes.ToSpaceSeparatedString(),
            };

            if (request.IsOpenIdRequest)
            {
                response.SessionState = GenerateSessionStateValue(request);
            }

            return response;
        }

        private string GenerateSessionStateValue(ValidatedAuthorizeRequest request)
        {
            var sessionId = request.SessionId;
            if (sessionId.IsMissing()) return null;

            var salt = CryptoRandom.CreateUniqueId();
            var clientId = request.ClientId;

            var uri = new Uri(request.RedirectUri);
            var origin = uri.Scheme + "://" + uri.Host;
            if (!uri.IsDefaultPort)
            {
                origin += ":" + uri.Port;
            }

            var bytes = Encoding.UTF8.GetBytes(clientId + origin + sessionId + salt);
            byte[] hash;

            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            return Base64Url.Encode(hash) + "." + salt;
        }

        private void RaiseCodeIssuedEvent(string id, AuthorizationCode code)
        {
            _events.RaiseAuthorizationCodeIssuedEvent(id, code);
        }
    }
}