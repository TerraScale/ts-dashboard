using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Profile;

public sealed record ProfilePage(ShellModel Shell, string Tab = "profile", string Error = "", string Status = "", IReadOnlyList<ProfileToken>? Tokens = null, CreatedProfileToken? CreatedToken = null, IReadOnlyList<DashboardSessionInfo>? Sessions = null, AccountMfaStatus? Mfa = null)
{
    public string? DraftName { get; init; }
    public string? DraftPhone { get; init; }
    public string[] Permissions
    {
        get
        {
            var result = new List<string>();
            if (Shell.Context.Project is not null)
            {
                result.Add("Access selected project");
                if (Shell.Context.CanWrite) result.Add("Write selected project data");
            }
            if (Shell.Context.CanManage) result.Add("Manage organization resources");
            if (Shell.Context.Organization?.CanProvision(Shell.Context.UserId) == true) result.Add("Provision organization resources");
            return result.ToArray();
        }
    }
}

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfile(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/profile", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IProfileTokens tokens, ITsAuthMfa mfa) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null) return AccountRequest.LoginRedirect(http);
            var tab = http.Request.Query["tab"].ToString();
            if (tab is not ("security" or "sessions" or "access-tokens" or "danger")) tab = "profile";
            var list = tab is "access-tokens" or "danger" ? await tokens.ListAsync(context, http.RequestAborted) : null;
            var active = await sessions.ListAsync(http, context.Session);
            var status = tab == "security" ? await mfa.GetAccountMfaAsync(context.Session, http.RequestAborted) : null;
            return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Tokens: list, Sessions: active, Mfa: status));
        });
        endpoints.MapPost("/api/v1/dashboard/profile", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            await SaveAsync(http, sessions, accounts, false));
        endpoints.MapPost("/api/v1/dashboard/profile/language", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            await SaveAsync(http, sessions, accounts, true));
        endpoints.MapPost("/api/v1/dashboard/profile/delete", AccountDeletion.DeleteAsync);
        // Public: the session ended with the account, so sending the user to /login would bounce through ts-auth with a stale credential.
        endpoints.MapGet("/account/deleted", (HttpContext http) => AccountDeletedView.Create(new DocumentModel("Account deleted", DocumentModel.NonceFor(http))));
        return endpoints;
    }

    private static async Task<IResult> SaveAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, bool language)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
        try
        {
            if (language)
            {
                var locale = form["locale"].ToString();
                if (locale is not ("en" or "pt-BR")) throw new AccountAccessException("Choose an available language.", 422);
                await accounts.UpdateAccountAsync(context.Session, account => account.Locale = locale, http.RequestAborted);
            }
            else
            {
                await ProfileChanges.SaveAsync(accounts, context.Session, form["name"].ToString(), form["phoneNumber"].ToString(), http.RequestAborted);
            }
            return HttpRedirects.SeeOther("/profile?tab=profile");
        }
        catch (AccountAccessException error)
        {
            var slice = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), Error: error.Message, Sessions: await sessions.ListAsync(http, context.Session))
            {
                DraftName = language ? null : form["name"].ToString(),
                DraftPhone = language ? null : form["phoneNumber"].ToString()
            });
            slice.StatusCode = error.StatusCode;
            return slice;
        }
    }
}
