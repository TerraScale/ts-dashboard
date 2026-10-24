using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Profile;

// ts-auth deletes the user, its sessions, tokens, memberships and the organizations only
// the user belongs to; the dashboard then drops its own records for the same scope.
// Paid plans of those organizations end immediately in Stripe, without a refund.
public static class AccountDeletion
{
    // ts-auth answers 202 while project closures finish in the background, and repeating the request resumes them. Closures
    // usually finish within seconds, so resume a few times before asking the user to try again.
    private static readonly TimeSpan[] ResumeDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private static async Task<bool> DeleteResumingAsync(ITsAuthPlatform platform, SessionRecord session, CancellationToken cancellationToken)
    {
        if (await platform.DeleteAccountAsync(session, cancellationToken)) return true;
        foreach (var delay in ResumeDelays)
        {
            await Task.Delay(delay, cancellationToken);
            if (await platform.DeleteAccountAsync(session, cancellationToken)) return true;
        }
        return false;
    }

    public static async Task<IResult> DeleteAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling billing)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
        var cancellationToken = http.RequestAborted;
        List<OrganizationRecord> sole = [];
        try
        {
            var typed = form["email"].ToString().Trim();
            if (typed.Length == 0 || !typed.Equals(context.Account.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new AccountAccessException("Type your account email to confirm deletion.", 422);
            foreach (var org in context.Organizations)
            {
                var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
                if (members.Any(member => member.UserId != context.UserId))
                {
                    // Checked before any subscription is canceled; ts-auth enforces the same rule below.
                    if (members.Any(member => member.UserId == context.UserId && member.Role == "owner") &&
                        !members.Any(member => member.UserId != context.UserId && member.Role == "owner"))
                        throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409);
                    continue;
                }
                if (org.Projects.Values.Any(project => project.Databases.Count > 0))
                    throw new AccountAccessException($"Delete your databases in {org.Name} before deleting your account.", 409);
                sole.Add(org);
            }
            // Shared organizations keep their subscriptions; only organizations deleted with the account are canceled.
            foreach (var org in sole) await billing.CancelForAccountDeletionAsync(org.Id, org.Name, cancellationToken);
            try
            {
                if (!await DeleteResumingAsync(platform, context.Session, cancellationToken))
                    throw new AccountAccessException("Account deletion is still in progress. Try again shortly; your dashboard records will stay until deletion is confirmed.", 409, "deletion_pending");
            }
            catch (AccountAccessException error) when (error.Code == "last_owner")
            { throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409); }
            // Dashboard records go only after ts-auth confirms; a 401/404 can mean an expired token or a ts-auth without the route.
        }
        catch (AccountAccessException error)
        {
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "danger", Error: error.Message,
                Tokens: await tokens.ListAsync(context, cancellationToken), Sessions: await sessions.ListAsync(http, context.Session)));
            view.StatusCode = error.StatusCode;
            return view;
        }
        foreach (var orgId in sole.Select(org => org.Id))
        {
            if (await state.GetAsync(StripeBilling.Collection, orgId, cancellationToken) is { } document &&
                StripeBilling.ReadBinding(document, orgId).CustomerId is { Length: > 0 } customer)
                await DropAsync(state, StripeBilling.CustomersCollection, customer, cancellationToken);
            await DropAsync(state, StripeBilling.Collection, orgId, cancellationToken);
            await DropAsync(state, AccountService.OrganizationsCollection, orgId, cancellationToken);
        }
        await tokens.ForgetAllAsync(context.UserId, cancellationToken);
        var key = AccountService.AccountStorageKey(context.UserId);
        await DropAsync(state, AccountService.AccountsCollection, key, cancellationToken);
        await DropAsync(state, AccountService.IdentitiesCollection, key, cancellationToken);
        await sessions.RevokeUserAsync(context.UserId, cancellationToken);
        await sessions.EndAsync(http);
        return HttpRedirects.SeeOther("/account/deleted");
    }

    private static async Task DropAsync(IDashboardState state, string collection, string id, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(collection, id, cancellationToken) is { } document)
            await state.DeleteAsync(collection, id, document.Version, cancellationToken);
    }
}
