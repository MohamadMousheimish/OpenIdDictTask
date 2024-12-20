﻿using AuthorizationServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationServer.Extensions;

public static class OpenIDdictExtension
{
    public static WebApplicationBuilder AddOpenIddict(this WebApplicationBuilder builder)
    {
        builder.Services
               .AddOpenIddict()
               .AddCore(options=>
               {
                   options.UseEntityFrameworkCore()
                          .UseDbContext<ApplicationDbContext>();
               })
               .AddServer(options =>
               {
                   options.AllowClientCredentialsFlow();
                   options.AllowAuthorizationCodeFlow()
                          .RequireProofKeyForCodeExchange();
                   options.SetTokenEndpointUris("connect/token")
                          .SetAuthorizationEndpointUris("connect/authorize");
                   options.AddDevelopmentEncryptionCertificate()
                          .AddDevelopmentSigningCertificate();
                   options.DisableAccessTokenEncryption();
                   options.UseAspNetCore()
                          .EnableTokenEndpointPassthrough()
                          .EnableAuthorizationEndpointPassthrough();
               });

        builder.Services.AddIdentity<IdentityUser, IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders()
            .AddDefaultUI();

        builder.Services.AddHostedService<DataSeeder>();

        return builder;
    }
}
