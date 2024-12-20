﻿using Microsoft.AspNetCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Primitives;

namespace AuthorizationServer.Endpoints;

public static class AuthorizationEndpoint
{
    public static WebApplication MapAuthorizationEndpoints(this WebApplication app)
    {
        app.MapPost("/connect/token", Exchange).WithOpenApi();
        app.MapGet("/connect/authorize", Authorize).WithOpenApi();
        app.MapPost("/connect/authorize", Authorize).WithOpenApi();

        return app;
    }

    public static async Task<IResult> Authorize
    (
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<IdentityUser> userManager
    )
    {
        var consentVerified = await VerifyConsent(httpContext, applicationManager, authorizationManager, scopeManager, userManager);
        if (consentVerified is not null)
            return consentVerified;

        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        AuthenticateResult result = await httpContext.AuthenticateAsync();
        if (result is null || !result.Succeeded || request.HasPromptValue(PromptValues.Login))
        {
            if (request.HasPromptValue(PromptValues.None))
                return Results.Forbid();

            string prompt = string.Join(" ", request.GetPromptValues().Remove(PromptValues.Login));

            var parameters = httpContext.Request.HasFormContentType ?
                httpContext.Request.Form.Where(parameter => parameter.Key != Parameters.Prompt).ToList() :
                httpContext.Request.Query.Where(parameter => parameter.Key != Parameters.Prompt).ToList();

            parameters.Add(KeyValuePair.Create(Parameters.Prompt, new StringValues(prompt)));

            return Results.Challenge(new AuthenticationProperties
            {
                RedirectUri = httpContext.Request.PathBase + httpContext.Request.Path + QueryString.Create(parameters)
            });
        }

        var user = await userManager.GetUserAsync(result.Principal) ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        var application = await applicationManager.FindByClientIdAsync(request.ClientId) ?? throw new InvalidOperationException("Details concerning the calling client application cannot be found.");

        IAsyncEnumerable<object> authorizationList = authorizationManager.FindAsync
        (
            subject: await userManager.GetUserIdAsync(user),
            client: await applicationManager.GetIdAsync(application),
            status: Statuses.Valid,
            type: AuthorizationTypes.Permanent,
            scopes: request.GetScopes()
        );

        List<object> authorizations = [];
        await foreach (object authorization in authorizationList)
        {
            authorizations.Add(authorization);
        }

        switch (await applicationManager.GetConsentTypeAsync(application))
        {
            case ConsentTypes.External when authorizations.Count is 0:
                return Results.Forbid();

            case ConsentTypes.Implicit:
            case ConsentTypes.External when authorizations.Count is not 0:
            case ConsentTypes.Explicit when authorizations.Count is not 0 && !request.HasPromptValue(PromptValues.Consent):

                ClaimsIdentity identity = new
                (
                    authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                    nameType: Claims.Name,
                    roleType: Claims.Role
                );

                identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
                        .SetClaim(Claims.Email, await userManager.GetEmailAsync(user))
                        .SetClaim(Claims.Name, await userManager.GetUserNameAsync(user))
                        .SetClaim(Claims.PreferredUsername, await userManager.GetUserNameAsync(user))
                        .SetClaims(Claims.Role, [.. (await userManager.GetRolesAsync(user))]);

                identity.SetScopes(request.GetScopes());
                IAsyncEnumerable<string>? scopeResources = scopeManager.ListResourcesAsync(identity.GetScopes());

                List<string> resources = [];
                await foreach (string resource in scopeResources)
                {
                    resources.Add(resource);
                }
                identity.SetResources(resources);

                var authorization = authorizations.LastOrDefault();
                authorization ??= await authorizationManager.CreateAsync
                (
                    identity: identity,
                    subject: await userManager.GetUserIdAsync(user),
                    client: await applicationManager.GetIdAsync(application),
                    type: AuthorizationTypes.Permanent,
                    scopes: identity.GetScopes()
                );

                identity.SetAuthorizationId(await authorizationManager.GetIdAsync(authorization));
                identity.SetDestinations(GetDestinations);

                return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            case ConsentTypes.Explicit when request.HasPromptValue(PromptValues.None):
            case ConsentTypes.Systematic when request.HasPromptValue(PromptValues.None):
                return Results.Forbid();

            default:
                string jsonData = $"{{  \"applicationName\": \"{await applicationManager.GetLocalizedDisplayNameAsync(application)}\", \"scope\": \"{request.Scope}\"  }}";
                httpContext.Session.SetString("ConsentData", jsonData);
                IEnumerable<KeyValuePair<string, StringValues>> parameters = httpContext.Request.HasFormContentType ? httpContext.Request.Form : httpContext.Request.Query;
                return Results.Redirect($"/Consent{QueryString.Create(parameters)}");
        }
    }

    public static async Task<IResult> Exchange
    (
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager
    )
    {
        var request = httpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType())
        {
            AuthenticateResult authenticateResult = await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            IdentityUser? user = await userManager.FindByIdAsync(authenticateResult.Principal.GetClaim(Claims.Subject));
            if (user is null)
                return Results.Forbid();

            if (!await signInManager.CanSignInAsync(user))
                return Results.Forbid();

            ClaimsIdentity identity = new
            (
                authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                nameType: Claims.Name,
                roleType: Claims.Role
            );

            identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
                    .SetClaim(Claims.Email, await userManager.GetEmailAsync(user))
                    .SetClaim(Claims.Name, await userManager.GetUserNameAsync(user))
                    .SetClaim(Claims.PreferredUsername, await userManager.GetUserNameAsync(user))
                    .SetClaims(Claims.Role, [.. (await userManager.GetRolesAsync(user))]);

            identity.SetDestinations(GetDestinations);

            ClaimsPrincipal principal = new(identity);

            return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new NotImplementedException("The specified grant type is not implemented.");
    }

    static async Task<IResult> Accept
    (
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<IdentityUser> userManager
    )
    {
        var request = httpContext.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var user = await userManager.GetUserAsync(httpContext.User) ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        var application = await applicationManager.FindByClientIdAsync(request.ClientId) ?? throw new InvalidOperationException("Details concerning the calling client application cannot be found.");

        IAsyncEnumerable<object> authorizationList = authorizationManager.FindAsync
        (
            subject: await userManager.GetUserIdAsync(user),
            client: await applicationManager.GetIdAsync(application),
            status: Statuses.Valid,
            type: AuthorizationTypes.Permanent,
            scopes: request.GetScopes()
        );
        List<object> authorizations = [];
        await foreach (object auth in authorizationList)
        {
            authorizations.Add(auth);
        }

        if (authorizations.Count is 0 && await applicationManager.HasConsentTypeAsync(application, ConsentTypes.External))
        {
            return Results.Forbid();
        }

        ClaimsIdentity identity = new
        (
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role
        );

        identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await userManager.GetEmailAsync(user))
                .SetClaim(Claims.Name, await userManager.GetUserNameAsync(user))
                .SetClaim(Claims.PreferredUsername, await userManager.GetUserNameAsync(user))
                .SetClaims(Claims.Role, [.. (await userManager.GetRolesAsync(user))]);

        identity.SetScopes(request.GetScopes());
        IAsyncEnumerable<string>? scopeResources = scopeManager.ListResourcesAsync(identity.GetScopes());
        List<string> resources = [];
        await foreach (string resource in scopeResources)
        {
            resources.Add(resource);
        }
        identity.SetResources(resources);

        object? authorization = authorizations.LastOrDefault();
        authorization ??= await authorizationManager.CreateAsync
        (
            identity: identity,
            subject: await userManager.GetUserIdAsync(user),
            client: await applicationManager.GetIdAsync(application),
            type: AuthorizationTypes.Permanent,
            scopes: identity.GetScopes()
        );

        identity.SetAuthorizationId(await authorizationManager.GetIdAsync(authorization));
        identity.SetDestinations(GetDestinations);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    static IResult Deny()
    {
        return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;

                if (claim.Subject is not null && claim.Subject.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;

                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;

                if (claim.Subject is not null && claim.Subject.HasScope(Scopes.Email))
                    yield return Destinations.IdentityToken;

                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;

                if (claim.Subject is not null && claim.Subject.HasScope(Scopes.Roles))
                    yield return Destinations.IdentityToken;

                yield break;

            case "AspNet.Identity.SecurityStamp": yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    static async Task<IResult?> VerifyConsent
    (
        HttpContext httpContext,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictScopeManager scopeManager,
        UserManager<IdentityUser> userManager
    )
    {
        if (httpContext.Request.Method != "POST")
            return null;

        if (httpContext.Request.Form.Where(parameter => parameter.Key == "submit.Accept").Any())
            return await Accept(httpContext, applicationManager, authorizationManager, scopeManager, userManager);

        if (httpContext.Request.Form.Where(parameter => parameter.Key == "submit.Deny").Any())
            return Deny();

        return null;
    }
}
