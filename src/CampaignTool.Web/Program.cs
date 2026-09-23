using Azure.Communication.Email;
using CampaignTool.Web;
using CampaignTool.Web.Components;
using CampaignTool.Web.Data;
using CampaignTool.Web.Endpoints;
using CampaignTool.Web.Services;
using CampaignTool.Web.Workers;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<AcsOptions>(builder.Configuration.GetSection("Acs"));
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhooks"));
builder.Services.Configure<SendingOptions>(builder.Configuration.GetSection("Sending"));
builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection("Import"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

var sql = builder.Configuration.GetConnectionString("Sql");
if (string.IsNullOrWhiteSpace(sql))
    throw new InvalidOperationException("ConnectionStrings:Sql is not set. Locally: dotnet user-secrets set \"ConnectionStrings:Sql\" \"<connection string>\" --project src/CampaignTool.Web");
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(sql, s => s.EnableRetryOnFailure()));

// Set in Azure by the Bicep; App Insights 3.x refuses to start without it, so skip it locally.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddMudServices();

// ACS when configured (Azure); otherwise sends are logged in memory (local runs, tests).
var acsConnection = builder.Configuration["Acs:ConnectionString"];
if (!string.IsNullOrWhiteSpace(acsConnection))
{
    builder.Services.AddSingleton(new EmailClient(acsConnection));
    builder.Services.AddSingleton<IEmailSender, AcsEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, InMemoryEmailSender>();
}
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<TestEmailService>();
builder.Services.AddScoped<EventProcessor>();
builder.Services.AddSingleton<ImportFileStore>();
builder.Services.AddScoped<ContactImportService>();
builder.Services.AddScoped<ContactService>();
builder.Services.AddSingleton<AssetStore>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddHostedService<CampaignWorker>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Single instance: apply migrations at startup.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// Before status-code re-execution, so a 404 on a public path is not re-checked as /not-found.
app.UseMiddleware<AllowedUsersMiddleware>();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthEndpoint();
app.MapAcsWebhookEndpoint();
app.MapExportEndpoints();
app.MapAssetEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
