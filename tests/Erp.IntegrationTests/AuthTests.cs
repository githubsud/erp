using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.IntegrationTests.Infrastructure;
using Erp.Modules.Core.Auth;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class AuthTests(ErpFixture fixture)
{
    public sealed record Session(string? AccessToken, string? RefreshToken, bool MfaRequired, string? MfaToken, bool MfaEnabled);
    public sealed record Setup(string Secret, string OtpAuthUri);

    private async Task<(TenantSession Tenant, HttpClient Anonymous)> NewTenantAsync() =>
        (await fixture.CreateTenantAsync(), fixture.Factory.CreateClient());

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, TenantSession t) =>
        client.PostJsonAsync("/api/auth/login", new { tenantCode = t.Code, email = $"owner@{t.Code}.qa", password = "Owner-Password-123" });

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_every_session()
    {
        var (t, anon) = await NewTenantAsync();
        var first = await (await LoginAsync(anon, t)).ReadAsync<Session>();
        first.RefreshToken.ShouldNotBeNull();

        var second = await anon.PostAsync<Session>("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        second.AccessToken.ShouldNotBeNull();
        second.RefreshToken.ShouldNotBe(first.RefreshToken);

        var withNewToken = fixture.Factory.CreateClient();
        withNewToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        (await withNewToken.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Re-using the rotated token looks like theft: it fails and the newer token is revoked too.
        var replay = await anon.PostJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        (await replay.ProblemAsync()).GetProperty("code").GetString().ShouldBe("auth.session_expired");
        var afterReplay = await anon.PostJsonAsync("/api/auth/refresh", new { refreshToken = second.RefreshToken });
        afterReplay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (t, anon) = await NewTenantAsync();
        var session = await (await LoginAsync(anon, t)).ReadAsync<Session>();
        (await anon.PostJsonAsync("/api/auth/logout", new { refreshToken = session.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await anon.PostJsonAsync("/api/auth/refresh", new { refreshToken = session.RefreshToken })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Two_factor_sign_in_requires_a_valid_authenticator_code()
    {
        var (t, anon) = await NewTenantAsync();
        var setup = await t.Client.PostAsync<Setup>("/api/auth/mfa/setup", new { });
        setup.OtpAuthUri.ShouldStartWith("otpauth://totp/");

        var wrong = await t.Client.PostJsonAsync("/api/auth/mfa/enable", new { code = "000000" });
        (await wrong.ProblemAsync()).GetProperty("code").GetString().ShouldBe("auth.invalid_code");

        var step = Totp.CurrentStep(DateTimeOffset.UtcNow);
        (await t.Client.PostAsync<Dictionary<string, bool>>("/api/auth/mfa/enable", new { code = Totp.Code(setup.Secret, step) }))["enabled"].ShouldBeTrue();

        // The password alone now only yields a challenge, not a session.
        var challenge = await (await LoginAsync(anon, t)).ReadAsync<Session>();
        challenge.MfaRequired.ShouldBeTrue();
        challenge.AccessToken.ShouldBeNull();
        challenge.MfaToken.ShouldNotBeNull();

        // The challenge token is not an access token.
        var misuse = fixture.Factory.CreateClient();
        misuse.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenge.MfaToken);
        (await misuse.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The code used to enable cannot be replayed; the next step's code completes sign-in.
        var replay = await anon.PostJsonAsync("/api/auth/mfa/verify", new { mfaToken = challenge.MfaToken, code = Totp.Code(setup.Secret, step) });
        replay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var session = await anon.PostAsync<Session>("/api/auth/mfa/verify", new { mfaToken = challenge.MfaToken, code = Totp.Code(setup.Secret, step + 1) });
        session.AccessToken.ShouldNotBeNull();
        session.MfaEnabled.ShouldBeTrue();
    }
}
