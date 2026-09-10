using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Reflection;
using TMS_API.Audit;
using TMS_API.DAL;
using TMS_API.Middleware;
using TMS_API.Models.DTO;
using TMS_API.Services;
using TMS_API.Helpers;
using TMS_SharedLibrary.Audit;
using TMS_SharedLibrary.Models;
using TMS_SharedLibrary.Helpers;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc;
var builder = WebApplication.CreateBuilder(args);
ApplyDeploymentConfiguration(builder);

var keyVaultName = builder.Configuration["KeyVaultName"];
var managedIdentityClientId = builder.Configuration["ManagedIdentity:ClientId"];

if (!string.IsNullOrWhiteSpace(keyVaultName))
{
    try
    {
        var credentialOptions = new DefaultAzureCredentialOptions();

        if (!string.IsNullOrWhiteSpace(managedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId = managedIdentityClientId;
        }

        var secretClient = new SecretClient(
            new Uri($"https://{keyVaultName}.vault.azure.net/"),
            new DefaultAzureCredential(credentialOptions));

        builder.Configuration.AddAzureKeyVault(
            secretClient,
            new Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationOptions());
    }
    catch (Exception ex) when (
        builder.Environment.IsDevelopment() &&
        (ex is CredentialUnavailableException || ex is AuthenticationFailedException || ex is RequestFailedException))
    {
        Console.WriteLine($"Key Vault configuration skipped in Development: {ex.Message}");
    }
}

//Serilog Configuration


var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs"); // Ensure Logs directory exists
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()

    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)

    // Only include audit logs
    .Filter.ByIncludingOnly(logEvent =>
        logEvent.RenderMessage().Contains("Action:")
    )

    .WriteTo.File(
        path: Path.Combine(logDirectory, "audit-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,                 // safer for IIS
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: "{Message}{NewLine}"
    )

    .CreateLogger();

builder.Logging.ClearProviders();

builder.Host.UseSerilog();

// Add services to the container.

builder.Services.AddControllers();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors.Select(error => error.ErrorMessage).ToArray());

        return new BadRequestObjectResult(new
        {
            message = "Validation failed.",
            errors
        });
    };
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(
options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "TMS_API",
            Version = "v1"
        });
        options.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                Implicit = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri($"{builder.Configuration["AzureAdTms:Instance"]}{builder.Configuration["AzureAdTms:TenantId"]}/oauth2/v2.0/authorize"),
                    Scopes = new Dictionary<string, string>
                {
                    { $"api://{builder.Configuration["AzureAdTms:ClientId"]}/User_Access", "Access the API as a user" }
                }
                }
            }
        });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
       {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "oauth2"
                }
            },
            new[] { $"api://{builder.Configuration["AzureAdTms:ClientId"]}/User_Access" }
        }
    });
    });
builder.Services.AddScoped<IAuditLogger, SerilogAuditLogger>();
builder.Services.AddScoped<IToyRepo, ToyRepo>();
builder.Services.AddScoped<IStudentRepo, StudentRepo>();
builder.Services.AddScoped<ITeacherRepo, TeacherRepo>();
builder.Services.AddScoped<IUserRepo, UserRepo>();
builder.Services.AddScoped<INotificationRepo, NotificationRepo>();
builder.Services.AddScoped<IOverdueToyService, OverdueToyService>();

builder.Services.AddHostedService<BackgroundSchedulerService>();
builder.Services.AddDbContext<K40TmsDdContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("EaglesConnection")));
builder.Services.AddAutoMapper(cfg =>
{
    cfg.AddProfile<StudentProfile>();
    cfg.AddProfile<TeacherProfile>();
    cfg.AddProfile<UserProfile>();
}, Assembly.GetExecutingAssembly());
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
// Add authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.Authority = $"{builder.Configuration["AzureAdTms:Instance"]}{builder.Configuration["AzureAdTms:TenantId"]}";
    options.Audience = builder.Configuration["AzureAdTms:ClientId"];
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = $"{builder.Configuration["AzureAdTms:Instance"]}{builder.Configuration["AzureAdTms:TenantId"]}/v2.0",
        ValidateAudience = true,
        ValidAudiences = new[]
        {
            builder.Configuration["AzureAdTms:ClientId"],
            $"api://{builder.Configuration["AzureAdTms:ClientId"]}"
        },
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});



builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<UserHelper>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
    });
});
var app = builder.Build();

app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "TMS_API v1");
    options.OAuthClientId(builder.Configuration["AzureAdTms:ClientId"]);
    options.OAuthUsePkce();
    options.OAuthScopeSeparator(" ");
    options.OAuthUseBasicAuthenticationWithAccessCodeGrant();
});

app.UseAuthentication();

app.UseMiddleware<AuditLoginMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();

static void ApplyDeploymentConfiguration(WebApplicationBuilder builder)
{
    var deploymentEnvironment = Environment.GetEnvironmentVariable("TMS_ENV");

    if (!string.IsNullOrWhiteSpace(deploymentEnvironment))
    {
        if (deploymentEnvironment.Equals("Test", StringComparison.OrdinalIgnoreCase))
        {
            builder.Configuration.AddJsonFile("appsettings.Test.json", optional: true, reloadOnChange: true);
            return;
        }

        if (deploymentEnvironment.Equals("Development", StringComparison.OrdinalIgnoreCase) ||
            deploymentEnvironment.Equals("Dev", StringComparison.OrdinalIgnoreCase))
        {
            builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
            return;
        }

        if (deploymentEnvironment.Equals("Production", StringComparison.OrdinalIgnoreCase) ||
            deploymentEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase))
        {
            builder.Configuration.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
            return;
        }
    }

    var hostName =
        Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") ??
        Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ??
        Environment.GetEnvironmentVariable("HOSTNAME");

    if (string.IsNullOrWhiteSpace(hostName))
    {
        return;
    }

    if (hostName.Contains("test", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration.AddJsonFile("appsettings.Test.json", optional: true, reloadOnChange: true);
    }
    else if (hostName.Contains("dev", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
    }
    else if (hostName.Contains("prod", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
    }
}
