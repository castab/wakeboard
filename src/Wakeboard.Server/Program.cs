using System.Net;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.HttpOverrides;

namespace Wakeboard;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = new AppPaths();
        if (args.Length > 0 && args[0].ToLowerInvariant() is "install" or "update" or "status" or "uninstall")
        {
            try { return ServiceCommands.Execute(args, paths); }
            catch (Exception error) { Console.Error.WriteLine($"Error: {error.Message}"); return 1; }
        }

        AppSettings settings;
        try { settings = SettingsStore.Load(paths); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseWindowsService(options => options.ServiceName = ServiceCommands.ServiceName);
        builder.WebHost.UseUrls($"http://0.0.0.0:{settings.Port}");
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<ConfigStore>();
        builder.Services.AddSingleton<NetworkService>();
        builder.Services.AddSingleton<PasskeyStore>();
        builder.Services.AddSingleton<PasskeyService>();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        var app = builder.Build();
        var loginLimiter = new LoginAttemptLimiter();
        var preAuthPaths = new[] { "/api/auth/login", "/api/auth/session", "/api/auth/passkey/options", "/api/auth/passkey/verify" };

        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            if (context.Request.Method is "POST" or "PUT" or "DELETE" && !AuthService.HasValidOrigin(context.Request))
            {
                await Results.Json(new { error = "Invalid request origin." }, statusCode: 403).ExecuteAsync(context); return;
            }
            if (preAuthPaths.Any(path => context.Request.Path == path)) { await next(); return; }
            var auth = context.RequestServices.GetRequiredService<AuthService>();
            if (!auth.VerifySession(context.Request.Cookies[AuthService.CookieName]))
            {
                await Results.Json(new { error = "Authentication required." }, statusCode: 401).ExecuteAsync(context); return;
            }
            await next();
        });

        app.MapPost("/api/auth/login", async (HttpContext context, AuthService auth) =>
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "local";
            var now = DateTimeOffset.UtcNow;
            if (loginLimiter.IsBlocked(key, now)) return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            var input = await context.Request.ReadFromJsonAsync<LoginInput>();
            if (input?.Password is null || !auth.VerifyPassword(input.Password))
            {
                loginLimiter.RecordFailure(key, now);
                return Results.Json(new { error = "Incorrect password." }, statusCode: 401);
            }
            loginLimiter.Clear(key);
            context.Response.Cookies.Append(AuthService.CookieName, auth.CreateSession(now), auth.CookieOptions(context.Request.IsHttps));
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/api/auth/logout", (HttpContext context, AuthService auth) =>
        {
            context.Response.Cookies.Delete(AuthService.CookieName, auth.CookieOptions(context.Request.IsHttps));
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/auth/session", async (HttpContext context, AuthService auth, PasskeyStore passkeyStore) =>
        {
            var authenticated = auth.VerifySession(context.Request.Cookies[AuthService.CookieName]);
            var passkeyAvailable = PasskeyPolicy.IsEligibleOrigin(context.Request)
                && (await passkeyStore.ListAsync(context.RequestAborted)).Any(item => item.RpId == PasskeyPolicy.DeriveRpId(context.Request));
            return Results.Ok(new { authenticated, passkeyAvailable });
        });

        app.MapPost("/api/auth/passkey/options", async (HttpContext context, PasskeyService passkeys) =>
        {
            try { return Results.Ok(await passkeys.BeginLoginAsync(context.Request, context.RequestAborted)); }
            catch (ArgumentException error) { return Results.Json(new { error = error.Message }, statusCode: 400); }
            catch (KeyNotFoundException error) { return Results.Json(new { error = error.Message }, statusCode: 404); }
        });
        app.MapPost("/api/auth/passkey/verify", async (HttpContext context, AuthService auth, PasskeyService passkeys) =>
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "local";
            var now = DateTimeOffset.UtcNow;
            if (loginLimiter.IsBlocked(key, now)) return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            var input = await context.Request.ReadFromJsonAsync<PasskeyLoginVerifyInput>();
            bool ok;
            try { ok = input is not null && await passkeys.CompleteLoginAsync(context.Request, input.State, input.Assertion, context.RequestAborted); }
            catch (ArgumentException error) { return Results.Json(new { error = error.Message }, statusCode: 400); }
            if (!ok)
            {
                loginLimiter.RecordFailure(key, now);
                return Results.Json(new { error = "Passkey sign-in failed." }, statusCode: 401);
            }
            loginLimiter.Clear(key);
            context.Response.Cookies.Append(AuthService.CookieName, auth.CreateSession(now), auth.CookieOptions(context.Request.IsHttps));
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/passkeys/register/options", async (HttpContext context, PasskeyService passkeys) =>
            await Api(async () => Results.Ok(await passkeys.BeginRegistrationAsync(context.Request, context.RequestAborted))));
        app.MapPost("/api/passkeys/register/verify", async (HttpContext context, PasskeyService passkeys) => await Api(async () =>
        {
            var input = await context.Request.ReadFromJsonAsync<PasskeyRegisterVerifyInput>()
                ?? throw new ArgumentException("Missing passkey registration payload.");
            var credential = await passkeys.CompleteRegistrationAsync(context.Request, input.State, input.Attestation, input.Label, context.RequestAborted);
            return Results.Json(PasskeyCredentialView.From(credential), statusCode: 201);
        }));
        app.MapGet("/api/passkeys", async (PasskeyStore passkeyStore, CancellationToken token) => await Api(async () =>
            Results.Ok((await passkeyStore.ListAsync(token)).Select(PasskeyCredentialView.From))));
        app.MapDelete("/api/passkeys/{id}", async (string id, PasskeyStore passkeyStore, CancellationToken token) => await Api(async () =>
        {
            await passkeyStore.DeleteAsync(id, token);
            return Results.Ok(new { ok = true });
        }));

        app.MapGet("/api/hosts", async (ConfigStore store, CancellationToken token) => await Api(async () => Results.Ok(await store.ListAsync(token))));
        app.MapPost("/api/hosts", async (HostInput input, ConfigStore store, CancellationToken token) => await Api(async () => Results.Json(await store.CreateAsync(input, token), statusCode: 201)));
        app.MapPut("/api/hosts/{id}", async (string id, HostInput input, ConfigStore store, CancellationToken token) => await Api(async () => Results.Ok(await store.UpdateHostAsync(id, input, token))));
        app.MapDelete("/api/hosts/{id}", async (string id, ConfigStore store, CancellationToken token) => await Api(async () => { await store.DeleteAsync(id, token); return Results.Ok(new { ok = true }); }));
        app.MapGet("/api/interfaces", (NetworkService network) => Results.Ok(network.ListAdapters()));
        app.MapPost("/api/hosts/{id}/wake", async (string id, WakeInput input, ConfigStore store, NetworkService network, CancellationToken token) => await Api(async () =>
        {
            var host = await store.GetAsync(id, token) ?? throw new KeyNotFoundException("Host not found.");
            var interfaceId = string.IsNullOrWhiteSpace(input.InterfaceId) ? host.PreferredInterfaceId : input.InterfaceId.Trim();
            return Results.Ok(await network.WakeAsync(host.MacAddress, interfaceId, token));
        }));
        app.MapPost("/api/hosts/{id}/status", async (string id, ConfigStore store, NetworkService network, CancellationToken token) => await Api(async () =>
        {
            var host = await store.GetAsync(id, token) ?? throw new KeyNotFoundException("Host not found.");
            if (string.IsNullOrWhiteSpace(host.CheckTarget)) throw new ArgumentException("Add an IP address or hostname before checking status.");
            return Results.Ok(await network.CheckLivenessAsync(host.CheckTarget, token));
        }));

        var embedded = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "EmbeddedUi");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });
        app.MapFallback(async context =>
        {
            var index = embedded.GetFileInfo("index.html");
            if (!index.Exists) { context.Response.StatusCode = 500; await context.Response.WriteAsync("Embedded UI is missing."); return; }
            context.Response.ContentType = "text/html; charset=utf-8";
            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });

        await app.RunAsync();
        return 0;
    }

    private static async Task<IResult> Api(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (KeyNotFoundException error) { return Results.Json(new { error = error.Message }, statusCode: 404); }
        catch (ArgumentException error) { return Results.Json(new { error = error.Message }, statusCode: 400); }
        catch (InvalidDataException error) { return Results.Json(new { error = error.Message }, statusCode: 500); }
        catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
    }
}
