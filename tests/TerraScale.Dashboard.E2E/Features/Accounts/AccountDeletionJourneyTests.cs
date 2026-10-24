using System.Net;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

public sealed class AccountDeletionJourneyTests
{
    private const string UserId = "test-user";

    [Test]
    public async Task WrongEmailOrSharedOwnershipRefusesAndDeletesNothing()
    {
        await using var scenario = await Scenario.StartAsync();
        var personal = scenario.PersonalOrganizationId();
        using (var wrong = await scenario.DeleteAsync("someone@example.com"))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await wrong.Content.ReadAsStringAsync()).Contains("Type your account email to confirm deletion.");
        }
        scenario.AddOrganization("Shared company", owner: UserId, member: "other-user");
        using (var lastOwner = await scenario.DeleteAsync("user@example.com"))
        {
            await Assert.That(lastOwner.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await lastOwner.Content.ReadAsStringAsync()).Contains("Transfer ownership of your shared organizations before deleting your account.");
        }
        await Assert.That(await scenario.ExistsAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(UserId))).IsTrue();
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, personal)).IsTrue();
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsTrue();
        using var profile = await scenario.Client.GetAsync("/profile?tab=danger");
        await Assert.That(profile.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task ConfirmedDeletionRemovesUserSoleOrganizationsTokensAndSession()
    {
        await using var scenario = await Scenario.StartAsync();
        var personal = scenario.PersonalOrganizationId();
        var shared = scenario.AddOrganization("Shared company", owner: "other-user", member: UserId);
        await HttpJourney.CreatePatAsync(scenario.Client);
        // The first read stores the shared organization's projection.
        using (var page = await scenario.Client.GetAsync("/profile?tab=danger"))
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Delete account");
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, shared)).IsTrue();

        using (var deleted = await scenario.DeleteAsync("  USER@example.com "))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(deleted.Headers.Location!.OriginalString).IsEqualTo("/account/deleted");
        }
        // TS-975: a public confirmation, not /login bouncing through ts-auth with the ended session.
        using (var confirmation = await scenario.Client.GetAsync("/account/deleted"))
        {
            await Assert.That(confirmation.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await confirmation.Content.ReadAsStringAsync()).Contains("Account deleted");
        }
        var key = AccountService.AccountStorageKey(UserId);
        await Assert.That(await scenario.ExistsAsync(AccountService.AccountsCollection, key)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.IdentitiesCollection, key)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccessTokens.Collection, key)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, personal)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.OrganizationsCollection, shared)).IsTrue().Because("shared organizations keep their data");
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsFalse();
        await Assert.That(scenario.Environment.Workspace.Organizations[shared].Members.ContainsKey(UserId)).IsFalse();
        using var session = await scenario.Client.GetAsync("/__test/session");
        await Assert.That(session.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task PaidSoleOrganizationEndsImmediatelyAndStripeFailureKeepsTheAccount()
    {
        await using var scenario = await Scenario.StartAsync(signIn: false);
        var suffix = Guid.NewGuid().ToString()[..8];
        var email = "paid-delete-" + suffix + "@example.test";
        await Billing.BillingPlanJourneyTests.SignUpPaidAsync(scenario.Client, email, "E2E-delete-" + suffix, suffix);
        var userId = scenario.Environment.Workspace.Organizations.Values.Single(org => org.Members.Values.Any(member => member.Email == email)).OwnerUserId;
        var personal = scenario.PersonalOrganizationId(userId);
        // The reported case: the owner scheduled the switch to Free, which keeps the subscription until renewal.
        var csrf = await HttpJourney.CsrfAsync(scenario.Client, "/organization?tab=billing");
        using (var free = await HttpJourney.PostFormAsync(scenario.Client, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "free" }))
            await Assert.That(free.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var binding = StripeBilling.ReadBinding((await scenario.Environment.State.GetAsync(StripeBilling.Collection, personal, default))!, personal);
        var stripe = scenario.Environment.BillingPlans;
        await Assert.That(stripe.SubscriptionStatus(binding.SubscriptionId)).IsEqualTo("active");

        stripe.SetScenario(binding.CustomerId, "offline");
        using (var failed = await scenario.DeleteAsync(email))
        {
            await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
            await Assert.That(await failed.Content.ReadAsStringAsync()).Contains("Stripe could not cancel the paid plan of");
        }
        await Assert.That(await scenario.ExistsAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(userId))).IsTrue();
        await Assert.That(await scenario.ExistsAsync(StripeBilling.Collection, personal)).IsTrue();
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsTrue();

        stripe.SetScenario(binding.CustomerId, "confirmed");
        using (var deleted = await scenario.DeleteAsync(email))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(deleted.Headers.Location!.OriginalString).IsEqualTo("/account/deleted");
        }
        await Assert.That(stripe.SubscriptionStatus(binding.SubscriptionId)).IsEqualTo("canceled");
        await Assert.That(stripe.Mutations(binding.CustomerId).Count(path => path == "DELETE /v1/subscriptions/" + binding.SubscriptionId)).IsEqualTo(1);
        await Assert.That(await scenario.ExistsAsync(StripeBilling.Collection, personal)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(StripeBilling.CustomersCollection, binding.CustomerId)).IsFalse();
        await Assert.That(await scenario.ExistsAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(userId))).IsFalse();
        await Assert.That(scenario.Environment.Workspace.Organizations.ContainsKey(personal)).IsFalse();
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required DemoEnvironment Environment { get; init; }
        public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
        public required HttpClient Client { get; init; }

        public static async Task<Scenario> StartAsync(bool signIn = true)
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var app = environment.CreateTestHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            if (signIn)
            {
                using var login = await HttpJourney.LoginAsync(client);
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }
            return new() { Environment = environment, App = app, Client = client };
        }

        public string PersonalOrganizationId(string userId = UserId)
        {
            lock (Environment.Workspace.Gate) return Environment.Workspace.Organizations.Values.Single(org => org.Members.ContainsKey(userId)).Id;
        }

        public string AddOrganization(string name, string owner, string member)
        {
            var org = new OrganizationRecord { Id = new TerraScaleIdGenerator().NewId(), Name = name, Slug = AccountService.Slug(name), OwnerUserId = owner, CreatedAt = DateTimeOffset.UtcNow };
            org.Members[owner] = new() { UserId = owner, Email = owner + "@example.com", Role = "owner", CreatedAt = org.CreatedAt };
            org.Members[member] = new() { UserId = member, Email = member + "@example.com", Role = "member", CreatedAt = org.CreatedAt };
            lock (Environment.Workspace.Gate) Environment.Workspace.Organizations[org.Id] = org;
            return org.Id;
        }

        public async Task<HttpResponseMessage> DeleteAsync(string email)
        {
            var csrf = await HttpJourney.CsrfAsync(Client, "/profile?tab=danger");
            return await HttpJourney.PostFormAsync(Client, "/api/v1/dashboard/profile/delete", new() { ["_csrf"] = csrf, ["email"] = email });
        }

        public async Task<bool> ExistsAsync(string collection, string id) => await Environment.State.GetAsync(collection, id, default) is not null;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
