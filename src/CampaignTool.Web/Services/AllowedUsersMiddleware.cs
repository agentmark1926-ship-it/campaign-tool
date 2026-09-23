using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>
/// App Service Authentication (Entra ID) signs the user in and injects X-MS-CLIENT-PRINCIPAL-NAME
/// (it strips any client-supplied copy). This checks that UPN against Auth:AllowedUsers.
/// Anonymous paths: /health, /unsubscribe/*, /webhooks/*.
/// In Development with no header present, requests are allowed so the app runs locally without Easy Auth.
/// </summary>
public class AllowedUsersMiddleware(RequestDelegate next, IOptionsMonitor<AuthOptions> auth, IWebHostEnvironment env, ILogger<AllowedUsersMiddleware> log)
{
    public const string PrincipalNameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";

    public static bool IsAnonymousPath(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/unsubscribe", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/webhooks", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsAnonymousPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var upn = context.Request.Headers[PrincipalNameHeader].ToString();
        if (string.IsNullOrEmpty(upn))
        {
            if (env.IsDevelopment())
            {
                await next(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Sign-in required. App Service Authentication is not enabled or did not sign you in.");
            return;
        }

        if (!auth.CurrentValue.IsAllowed(upn))
        {
            log.LogWarning("Rejected sign-in for {Upn}: not in Auth:AllowedUsers", upn);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync($"{upn} is not allowed to use this app. Ask the owner to add you to Auth:AllowedUsers.");
            return;
        }

        await next(context);
    }
}
