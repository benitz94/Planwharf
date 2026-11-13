// Port of the module-only cases in engine/provider-accounts.test.cjs.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Deferred from engine/provider-accounts.test.cjs (they need the server or another module):
// - launchSpec "account changed before launch" needs engine/provider-runtime.cjs.
// - "account switching isolates roles, invalidates quota and stale access, and survives restart"
//   needs server.cjs, the orchestrator, catalog settings and quota.
// - "failed cache invalidation does not commit switched account identity and retries safely"
//   needs server.cjs and orchestrator durability (provider-model-catalogs.json).
public class ProviderAccountsTests
{
    const string CodexBusinessKey = "737495b6407ee1e150238f77d95ea135eb5db6cd68233d3bb5545da4956317ff";
    const string GrokTeamKey = "a7a099cee17b228a197856f5558ecb3c1f72ebdcc860a737b648c438605bdfd3";
    const string GrokPersonalKey = "8ef80143bcd91d83680ea04db6bfc5381e1e5d9c4455c0b5e4fe9b27d5a5b6b3";

    [Fact(DisplayName = "provider metadata distinguishes business from paid personal accounts without exposing credentials")]
    public void Provider_metadata_distinguishes_business_from_paid_personal_accounts_without_exposing_credentials()
    {
        var home = Directory.CreateTempSubdirectory("pw-account-metadata-").FullName;
        try
        {
            Write(home, new { tokens = new { account_id = "workspace", id_token = Jwt(new { sub = "fixture-user", auth = new { chatgpt_account_id = "workspace", chatgpt_plan_type = "enterprise" } }) } }, codex: true);
            var business = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("business", business.Type);
            Assert.Equal(CodexBusinessKey, business.Key);
            Assert.Equal("GPT fixture Business", ProviderAccounts.AccountLabel("GPT fixture", business));

            Write(home, new { tokens = new { account_id = "workspace", id_token = Jwt(new { sub = "fixture-user", auth = new { chatgpt_account_id = "workspace", chatgpt_plan_type = "pro" } }) } }, codex: true);
            var normal = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("normal", normal.Type);
            Assert.Equal(business.Key, normal.Key);
            Assert.Equal("GPT fixture", ProviderAccounts.AccountLabel("GPT fixture", normal));

            Write(home, new { scope = new { key = "fixture-token", user_id = "fixture-user", principal_type = "Team", team_id = "fixture-team" } }, codex: false);
            var team = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("business", team.Type);
            Assert.Equal(GrokTeamKey, team.Key);

            Write(home, new { scope = new { key = "rotated-fixture-token", user_id = "fixture-user", principal_type = "Team", team_id = "fixture-team" } }, codex: false);
            Assert.Equal(team.Key, ProviderAccounts.GrokAccount("grok", home).Key);

            Write(home, new { scope = new { key = "fixture-token", user_id = "fixture-user", principal_type = "User", email = "someone@company.example" } }, codex: false);
            var personal = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("normal", personal.Type);
            Assert.Equal(GrokPersonalKey, personal.Key);
            Assert.NotEqual(personal.Key, team.Key);

            foreach (var account in new[] { business, normal, team, personal })
                AssertNoSecrets(account, "fixture-user", "fixture-token", "company.example", "id_token", "rotated-fixture-token");

            Write(home, new { scope = new { key = "opaque-token", email = "someone@company.example" } }, codex: false);
            var unread = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("unread", unread.Type);
            Assert.Null(unread.Key);
            AssertNoSecrets(unread, "opaque-token", "someone@company.example", "fixture-token");
        }
        finally { Directory.Delete(home, true); }
    }

    static void Write(string home, object value, bool codex)
    {
        var json = JsonSerializer.Serialize(value);
        // Codex claims live under this URI. Grok fixtures keep the JS property name "scope".
        if (codex) json = json.Replace("\"auth\":", "\"https://api.openai.com/auth\":", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(home, "auth.json"), json);
    }

    static void WriteRaw(string home, string json) => File.WriteAllText(Path.Combine(home, "auth.json"), json);

    static string Jwt(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        json = json.Replace("\"auth\":", "\"https://api.openai.com/auth\":", StringComparison.Ordinal);
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "fixture." + body + ".fixture";
    }

    static void AssertNoSecrets(ProviderAccounts.AccountMetadata account, params string[] forbidden)
    {
        var text = account.Key + "\n" + account.Type + "\n" + account.Source + "\n" + account.CheckedAt;
        foreach (var item in forbidden) Assert.DoesNotContain(item, text, StringComparison.Ordinal);
        Assert.Contains(account.Type, new[] { "business", "normal", "unread" });
        Assert.Contains(account.Source, new[] { "Codex login metadata", "Grok login metadata", "Account metadata not exposed" });
        if (account.Key is not null) Assert.Matches("^[0-9a-f]{64}$", account.Key);
        if (account.CheckedAt is not null)
        {
            Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$", account.CheckedAt);
            var when = DateTime.Parse(account.CheckedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            Assert.InRange((DateTime.UtcNow - when.ToUniversalTime()).Duration().TotalSeconds, 0, 120);
        }
    }

    [Theory]
    [InlineData("business", "business")]
    [InlineData("TEAM", "business")]
    [InlineData("enterprise", "business")]
    [InlineData("edu", "business")]
    [InlineData("self_serve_business", "business")]
    [InlineData("self_serve_business_acme_team", "business")]
    [InlineData("enterprise_cbp", "business")]
    [InlineData("enterprise_cbp_foo_bar", "business")]
    [InlineData("free", "normal")]
    [InlineData("go", "normal")]
    [InlineData("plus", "normal")]
    [InlineData("PRO", "normal")]
    [InlineData("prolite", "normal")]
    [InlineData("personal", "normal")]
    [InlineData("individual", "normal")]
    [InlineData("user", "normal")]
    [InlineData("self_serve_business_1", "unread")]
    [InlineData("premium", "unread")]
    [InlineData("", "unread")]
    [InlineData(null, "unread")]
    public void Account_type_uses_the_javascript_plan_buckets(string? plan, string expected) =>
        Assert.Equal(expected, ProviderAccounts.AccountType(plan));

    [Fact]
    public void Unknown_account_has_no_key_and_business_labels_only_mark_business()
    {
        var unknown = ProviderAccounts.UnknownAccount();
        Assert.Null(unknown.Key);
        Assert.Equal("unread", unknown.Type);
        Assert.Equal("Account metadata not exposed", unknown.Source);
        Assert.Null(unknown.CheckedAt);
        Assert.Equal("GPT fixture", ProviderAccounts.AccountLabel("GPT fixture", unknown));
        Assert.Equal("GPT fixture", ProviderAccounts.AccountLabel("GPT fixture", null));
        Assert.Equal("GPT fixture Business", ProviderAccounts.AccountLabel("GPT fixture", new ProviderAccounts.AccountMetadata("abc", "business", "Codex login metadata", null)));
    }

    [Fact]
    public void Sensitive_nested_and_malformed_fields_cannot_appear_in_metadata()
    {
        var home = Directory.CreateTempSubdirectory("pw-account-secrets-").FullName;
        try
        {
            var jwt = Jwt(new
            {
                sub = "fixture-user",
                email = "jwt@company.example",
                password = "jwt-password-secret",
                auth = new { chatgpt_account_id = "workspace", chatgpt_plan_type = "plus", chatgpt_user_id = "fixture-user", email = "claim@company.example" }
            });
            WriteRaw(home, "{\"tokens\":{\"account_id\":\"workspace\",\"id_token\":" + JsonSerializer.Serialize(jwt) + ",\"access_token\":\"access-secret-value\",\"refresh_token\":\"refresh-secret-value\",\"email\":\"nested@company.example\"},\"email\":\"top@company.example\",\"password\":\"top-password-secret\"}");
            var codex = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("normal", codex.Type);
            Assert.Equal(CodexBusinessKey, codex.Key);
            AssertNoSecrets(codex, "fixture-user", "jwt@company.example", "jwt-password-secret", "claim@company.example", "access-secret-value", "refresh-secret-value", "nested@company.example", "top@company.example", "top-password-secret", "id_token", jwt);

            WriteRaw(home, "{\"tokens\":{\"account_id\":\"workspace\",\"id_token\":\"raw-id-token-secret\",\"access_token\":" + JsonSerializer.Serialize(jwt) + ",\"email\":\"blocked@company.example\"}}");
            var blocked = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("unread", blocked.Type);
            Assert.Equal("eded403f2c2fc321008193b369aad911faf57a212f32a98054add4f77d29769b", blocked.Key);
            AssertNoSecrets(blocked, "raw-id-token-secret", "blocked@company.example", jwt, "fixture-user");

            WriteRaw(home, "{\"tokens\":{\"account_id\":[\"workspace\"],\"id_token\":" + JsonSerializer.Serialize(jwt) + "}}");
            var arrayId = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("unread", arrayId.Type);
            Assert.Equal(CodexBusinessKey, arrayId.Key);

            WriteRaw(home, "{\"tokens\":{\"account_id\":\"1\",\"id_token\":" + JsonSerializer.Serialize(Jwt(new { sub = "fixture-user", auth = new { chatgpt_account_id = 1, chatgpt_plan_type = "pro" } })) + "}}");
            Assert.Equal("unread", ProviderAccounts.CodexAccount("codex", home).Type);

            WriteRaw(home, "\uFEFF{\"tokens\":{\"account_id\":\"workspace\",\"id_token\":" + JsonSerializer.Serialize(jwt) + "}}");
            Assert.Equal("normal", ProviderAccounts.CodexAccount("codex", home).Type);

            foreach (var broken in new[] { "{", "not json", "\"someone@company.example\"", "null", "1", "" })
            {
                WriteRaw(home, broken);
                var failed = ProviderAccounts.CodexAccount("codex", home);
                Assert.Equal("unread", failed.Type);
                Assert.Null(failed.Key);
                Assert.Equal("Codex login metadata", failed.Source);
                AssertNoSecrets(failed, "someone@company.example", "not json");
            }
            File.Delete(Path.Combine(home, "auth.json"));
            Directory.CreateDirectory(Path.Combine(home, "auth.json"));
            var directory = ProviderAccounts.CodexAccount("codex", home);
            Assert.Null(directory.Key);
            Assert.Equal("unread", directory.Type);
            Directory.Delete(Path.Combine(home, "auth.json"));

            File.Delete(Path.Combine(home, "auth.json"));
            var missing = ProviderAccounts.CodexAccount("codex", home);
            Assert.Null(missing.Key);
            Assert.Equal("unread", missing.Type);
            Assert.Equal("Codex login metadata", missing.Source);

            WriteRaw(home, "{\"tokens\":{\"account_id\":\"workspace\",\"id_token\":" + JsonSerializer.Serialize(jwt) + "}}");
            Assert.Equal(CodexBusinessKey, ProviderAccounts.CodexAccount("", home).Key);
            Assert.Equal(CodexBusinessKey, ProviderAccounts.CodexAccount(null, home).Key);
            Assert.NotEqual(CodexBusinessKey, ProviderAccounts.CodexAccount("Codex", home).Key);

            WriteRaw(home, "{\"one\":{\"key\":\"secret-one\",\"user_id\":\"user-one\",\"principal_type\":\"User\",\"email\":\"one@company.example\"},\"two\":{\"key\":\"secret-two\",\"user_id\":\"user-two\",\"principal_type\":\"User\",\"email\":\"two@company.example\",\"refresh_token\":\"refresh-two\"}}");
            var guessed = ProviderAccounts.GrokAccount("grok", home);
            Assert.Null(guessed.Key);
            Assert.Equal("unread", guessed.Type);
            AssertNoSecrets(guessed, "secret-one", "secret-two", "user-one", "user-two", "one@company.example", "two@company.example", "refresh-two");

            WriteRaw(home, "{\"https://auth.x.ai\":{},\"other\":{\"key\":\"secret-other\",\"user_id\":\"fixture-user\",\"principal_type\":\"User\",\"email\":\"other@company.example\"}}");
            var preferredEmpty = ProviderAccounts.GrokAccount("grok", home);
            Assert.Null(preferredEmpty.Key);
            AssertNoSecrets(preferredEmpty, "secret-other", "fixture-user", "other@company.example");

            WriteRaw(home, "{\"key\":\"top-secret-token\",\"user_id\":\"fixture-user\",\"principal_type\":\"User\",\"email\":\"top@company.example\",\"https://auth.x.ai\":{\"key\":\"nested-secret\",\"user_id\":\"nested-user\",\"principal_type\":\"Team\",\"team_id\":\"fixture-team\",\"email\":\"nested@company.example\"}}");
            var top = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("normal", top.Type);
            Assert.Equal(GrokPersonalKey, top.Key);
            AssertNoSecrets(top, "top-secret-token", "top@company.example", "nested-secret", "nested-user", "nested@company.example", "fixture-team", "fixture-user");

            WriteRaw(home, "{\"https://accounts.x.ai/sign-in\":{\"user_id\":\"fixture-user\",\"principal_type\":\"User\",\"principal_id\":\"org-1\",\"key\":\"sign-in-secret\",\"email\":\"sign@company.example\"},\"other\":{\"user_id\":\"other-user\",\"key\":\"other-secret\",\"principal_type\":\"Team\",\"team_id\":\"fixture-team\",\"email\":\"other@company.example\"}}");
            var signIn = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("normal", signIn.Type);
            Assert.Equal("e7057b4fef298c7f9dbd22a69d6e85772366d0311b0c5bf26a5b51aaafb4c8bd", signIn.Key);
            AssertNoSecrets(signIn, "sign-in-secret", "sign@company.example", "other-secret", "other-user", "other@company.example", "fixture-user", "org-1");

            WriteRaw(home, "{\"scope\":{\"refresh_token\":\"refresh-secret-value\",\"user_id\":\"someone@company.example\",\"principal_type\":\"User\",\"email\":\"someone@company.example\"}}");
            var emailId = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("normal", emailId.Type);
            Assert.Equal("40189f20ecc24519a0b2e74cb5d37c4d6597337a099cadb58d63a62368f0765d", emailId.Key);
            AssertNoSecrets(emailId, "refresh-secret-value", "someone@company.example");

            WriteRaw(home, "{\"a\":{\"refresh_token\":0,\"user_id\":\"zero-user\",\"email\":\"zero@company.example\"},\"b\":{\"key\":\"only-real-token\",\"user_id\":\"fixture-user\",\"principal_type\":\"User\"}}");
            Assert.Equal(GrokPersonalKey, ProviderAccounts.GrokAccount("grok", home).Key);

            WriteRaw(home, "[{\"key\":\"array-token\",\"user_id\":\"fixture-user\",\"principal_type\":\"Team\",\"team_id\":\"fixture-team\",\"email\":\"array@company.example\"}]");
            var asArray = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal(GrokTeamKey, asArray.Key);
            AssertNoSecrets(asArray, "array-token", "array@company.example", "fixture-user", "fixture-team");

            WriteRaw(home, "[{\"key\":\"array-token\",\"user_id\":\"one\",\"principal_type\":\"User\"},{\"refresh_token\":\"second-refresh\",\"user_id\":\"two\",\"principal_type\":\"User\",\"email\":\"two@company.example\"}]");
            var two = ProviderAccounts.GrokAccount("grok", home);
            Assert.Null(two.Key);
            AssertNoSecrets(two, "array-token", "second-refresh", "two@company.example");

            WriteRaw(home, "{\"scope\":{\"key\":\"not.a.jwt\",\"user_id\":\"fixture-user\",\"principal_type\":\"Team\"}}");
            var noTeamId = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("unread", noTeamId.Type);
            Assert.Equal(GrokPersonalKey, noTeamId.Key);
            AssertNoSecrets(noTeamId, "not.a.jwt", "fixture-user");
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void Access_token_fallback_account_mismatch_and_grok_subject_stay_display_only()
    {
        var home = Directory.CreateTempSubdirectory("pw-account-edges-").FullName;
        try
        {
            var access = Jwt(new { sub = "fixture-user", auth = new { chatgpt_account_id = "workspace", chatgpt_plan_type = "enterprise" } });
            WriteRaw(home, "{\"tokens\":{\"account_id\":\"workspace\",\"access_token\":" + JsonSerializer.Serialize(access) + ",\"refresh_token\":\"refresh-secret-value\",\"email\":\"access@company.example\"}}");
            var fromAccess = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("business", fromAccess.Type);
            Assert.Equal(CodexBusinessKey, fromAccess.Key);
            AssertNoSecrets(fromAccess, access, "refresh-secret-value", "access@company.example", "fixture-user", "id_token");

            var mismatched = Jwt(new { sub = "fixture-user", email = "mismatch@company.example", auth = new { chatgpt_account_id = "other-workspace", chatgpt_plan_type = "enterprise" } });
            WriteRaw(home, "{\"tokens\":{\"account_id\":\"workspace\",\"id_token\":" + JsonSerializer.Serialize(mismatched) + "}}");
            var unread = ProviderAccounts.CodexAccount("codex", home);
            Assert.Equal("unread", unread.Type);
            Assert.Equal(CodexBusinessKey, unread.Key);
            AssertNoSecrets(unread, "other-workspace", "mismatch@company.example", mismatched, "fixture-user");

            var subject = Jwt(new { sub = "fixture-user", email = "subject@company.example" });
            WriteRaw(home, "{\"https://auth.x.ai\":\"subject@company.example\",\"spare\":{\"key\":" + JsonSerializer.Serialize(subject) + ",\"principal_type\":\"User\",\"email\":\"spare@company.example\"}}");
            var blockedScope = ProviderAccounts.GrokAccount("grok", home);
            Assert.Null(blockedScope.Key);
            Assert.Equal("unread", blockedScope.Type);
            AssertNoSecrets(blockedScope, "subject@company.example", "spare@company.example", subject, "fixture-user");

            WriteRaw(home, "{\"scope\":{\"key\":" + JsonSerializer.Serialize(subject) + ",\"principal_type\":\"User\",\"email\":\"subject@company.example\"}}");
            var fromSubject = ProviderAccounts.GrokAccount("grok", home);
            Assert.Equal("normal", fromSubject.Type);
            Assert.Equal(GrokPersonalKey, fromSubject.Key);
            AssertNoSecrets(fromSubject, subject, "subject@company.example", "fixture-user");
        }
        finally { Directory.Delete(home, true); }
    }
}
