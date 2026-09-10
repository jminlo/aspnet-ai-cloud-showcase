using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Localization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using System.Globalization;
using TMSStudent.Services;

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

builder.Services.Configure<CookiePolicyOptions>(options => {
    options.CheckConsentNeeded = context => true;
    options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
    options.HandleSameSiteCookieCompatibility();
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.Name = ".AspNetCore.Student.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

var auth = builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(options => {
        builder.Configuration.Bind("AzureAdTms", options);
        options.UsePkce = true;
        options.SaveTokens = false;
        options.ResponseType = "code";
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        options.Events.OnTokenValidated += async context => {
            var roleClaims = context.Principal.FindAll("roles");
            var identity = (System.Security.Claims.ClaimsIdentity)context.Principal.Identity;
            foreach (var roleClaim in roleClaims)
            {
                identity.AddClaim(new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.Role,
                    roleClaim.Value));
            }

            var oidClaim = context.Principal.FindFirst("oid") ??
                          context.Principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier");
            if (oidClaim != null && !context.Principal.HasClaim(c => c.Type == "oid"))
            {
                identity.AddClaim(new System.Security.Claims.Claim("oid", oidClaim.Value));
            }
        };

        options.Events.OnSignedOutCallbackRedirect = context => {
            context.Response.Redirect("/Home/Login");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    })
    .EnableTokenAcquisitionToCallDownstreamApi(new[] {
        "User.Read",
        "User.Read.All",
        "api://70294acc-b64c-4cf7-8b50-f57d6b797971/User_Access"
    })
    .AddDistributedTokenCaches();

builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options => {
    options.Cookie.Name = ".AspNetCore.Student.Cookies";
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.IsEssential = true;
    options.AccessDeniedPath = "/Home/AccessDenied";
    options.LoginPath = "/Home/Login";
});

builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.NonceCookie.Name = ".AspNetCore.Student.Nonce";
    options.CorrelationCookie.Name = ".AspNetCore.Student.Correlation";
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization()
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorPages()
    .AddMicrosoftIdentityUI();

var apiBase = builder.Configuration["Api:BaseUrl"] ?? "https://localhost:5001/api/";

if (string.IsNullOrEmpty(apiBase))
{
    throw new InvalidOperationException("Api:BaseUrl is not configured in appsettings.json");
}

builder.Services.AddTransient<AuthenticationDelegatingHandler>();

builder.Services.AddHttpClient<IToyAPIService, ToyAPIService>(client => {
    client.BaseAddress = new Uri(apiBase);
})
.AddHttpMessageHandler<AuthenticationDelegatingHandler>();

builder.Services.AddHttpClient<IStudentAPIService, StudentAPIService>(client => {
    client.BaseAddress = new Uri(apiBase);
})
.AddHttpMessageHandler<AuthenticationDelegatingHandler>();

builder.Services.AddHttpClient<INotificationService, NotificationService>(client => {
    client.BaseAddress = new Uri(apiBase);
})
.AddHttpMessageHandler<AuthenticationDelegatingHandler>();

builder.Services.AddHttpClient<ITeacherAPIService, TeacherAPIService>(client => {
    client.BaseAddress = new Uri(apiBase);
})
.AddHttpMessageHandler<AuthenticationDelegatingHandler>();

builder.Services.AddHttpClient<IUserAPIService, UserAPIService>(client => {
    client.BaseAddress = new Uri(apiBase);
})
.AddHttpMessageHandler<AuthenticationDelegatingHandler>();

builder.Services.AddHttpClient("TmsApi", client => {
    client.BaseAddress = new Uri(apiBase);
})
.AddHttpMessageHandler<AuthenticationDelegatingHandler>();


var app = builder.Build();

var supportedCultures = new[]
{
    new CultureInfo("en-CA"),
    new CultureInfo("fr-CA")
};

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en-CA"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures,
    RequestCultureProviders = new IRequestCultureProvider[]
    {
        new CookieRequestCultureProvider()
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

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

    if (hostName.Contains("cstest", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration.AddJsonFile("appsettings.Test.json", optional: true, reloadOnChange: true);
    }
    else if (hostName.Contains("csdev", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
    }
    else if (hostName.Contains("csprod", StringComparison.OrdinalIgnoreCase))
    {
        builder.Configuration.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
    }
}
