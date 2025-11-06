using System;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using DotNetEnv;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Amazon.S3;
using firstconnectfirebase.Services;

var builder = WebApplication.CreateBuilder(args);

// Load .env only on dev
if (builder.Environment.IsDevelopment())
{
    try { Env.Load(); } catch { /* ignore if .env not present */ }
}

// MVC + HTTP facilities
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

// Session cookie hardened for Azure
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Trust Azure proxy (prevents HTTPS redirect loops)
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

/* -------- Firebase Admin SDK (env/app settings only; no local files) -------- */
var fbB64 = builder.Configuration["FIREBASE_CONFIG_B64"];
var fbJson = builder.Configuration["FIREBASE_CONFIG_JSON"];
if (FirebaseApp.DefaultInstance == null)
{
    if (!string.IsNullOrWhiteSpace(fbB64))
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(fbB64));
        FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromJson(json) });
    }
    else if (!string.IsNullOrWhiteSpace(fbJson))
    {
        FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromJson(fbJson) });
    }
    // else: skip; REST auth via Web API key still works
}
/* --------------------------------------------------------------------------- */


// (Optional) if some controllers still inject FirebaseService concrete type:
builder.Services.AddScoped<FirebaseService, FirebaseService>();

/* ------------------------ Conditional AWS S3 registration ------------------- */
// Read both ":" and "__" forms for compatibility with Azure/.env
string awsKey = builder.Configuration["AWS:AccessKey"] ?? builder.Configuration["AWS__AccessKey"];
string awsSecret = builder.Configuration["AWS:SecretKey"] ?? builder.Configuration["AWS__SecretKey"];
string awsRegion = builder.Configuration["AWS:Region"] ?? builder.Configuration["AWS__Region"];
string awsBucket = builder.Configuration["AWS:BucketName"] ?? builder.Configuration["AWS__BucketName"];

bool hasS3 = !string.IsNullOrWhiteSpace(awsKey)
          && !string.IsNullOrWhiteSpace(awsSecret)
          && !string.IsNullOrWhiteSpace(awsRegion)
          && !string.IsNullOrWhiteSpace(awsBucket);

if (hasS3)
{
    builder.Services.AddSingleton<IAmazonS3>(_ =>
        new AmazonS3Client(awsKey, awsSecret, Amazon.RegionEndpoint.GetBySystemName(awsRegion)));
    builder.Services.AddSingleton<S3Service>();
}
/* --------------------------------------------------------------------------- */

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.UseSession();

// Health & config probes (no secrets)
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
app.MapGet("/configz", (IConfiguration cfg) => Results.Ok(new
{
    hasWebApiKey = !string.IsNullOrWhiteSpace(cfg["Firebase:WebApiKey"]) || !string.IsNullOrWhiteSpace(cfg["Firebase__WebApiKey"]),
    hasDbUrl = !string.IsNullOrWhiteSpace(cfg["Firebase:DatabaseUrl"]) || !string.IsNullOrWhiteSpace(cfg["Firebase__DatabaseUrl"]),
    adminReady = FirebaseApp.DefaultInstance != null,
    hasS3 = hasS3
}));



// Let Home/Index decide: if no session → it redirects to /Auth/Login
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
