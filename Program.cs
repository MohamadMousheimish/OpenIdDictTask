using AuthorizationServer.Data;
using AuthorizationServer.Endpoints;
using AuthorizationServer.Extensions;
using Microsoft.EntityFrameworkCore;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromMinutes(5);
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
        });

        builder.AddOpenIddict();

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseInMemoryDatabase(nameof(ApplicationDbContext));
            options.UseOpenIddict();
        });

        builder.Services.AddHttpClient("TokenApiClient", client =>
        {
            client.BaseAddress = new Uri("https://localhost:4001/");
        });

        builder.Services.AddRazorPages();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseSession();

        app.UseAuthentication();

        app.MapAuthorizationEndpoints();
        app.MapApplicationEndpoints();

        app.MapRazorPages();

        app.Run();
    }
}