using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Strata.Host.Scanning;
using Strata.Storage;

namespace Strata.Host.Api;

/// <summary>Where the API listens, and what it will and will not accept.</summary>
internal sealed record ApiOptions
{
    /// <summary>The port from the brief.</summary>
    public int Port { get; init; } = 5177;

    /// <summary>Log a line per request with its duration.</summary>
    public bool LogRequests { get; init; }
}

/// <summary>
/// Builds the process the product actually ships as: one executable holding the scanner and the API.
/// </summary>
/// <remarks>
/// Both halves live in one process on purpose. They share a database, and the scanner's whole
/// resource discipline — per-thread background I/O priority rather than per-process — was chosen
/// precisely so that a scan cannot throttle the UI reading from the same process. Splitting them
/// would mean two processes contending for one SQLite file for no benefit.
/// </remarks>
internal static partial class ApiHost
{
    internal static WebApplication Build(StrataDatabase database, ApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(c => c.SingleLine = true);

        // Strata's own lines at Information, the framework's at Warning. The point of the console
        // output is judging scanner behaviour, and three lines of routing per request buries it.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Strata", LogLevel.Information);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Loopback interfaces only, and stated as addresses rather than as the string
            // "localhost" so there is nothing left to interpret. Local-only is a hard constraint
            // from the brief, not a default worth leaving to configuration: an explicit Listen also
            // overrides anything ASPNETCORE_URLS might try to bind.
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.Listen(IPAddress.IPv6Loopback, options.Port);

            kestrel.AddServerHeader = false;
        });

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

            // Enums as names. "grew" survives a schema change; 1 does not, and a UI that has to keep
            // its own copy of the mapping is a UI with a stale copy of the mapping.
            json.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        builder.Services.AddSingleton(database);

        // Constructed by hand rather than by the container, so the scheduler's constructor can stay
        // internal along with the rest of the type.
        builder.Services.AddSingleton(sp =>
            new ScanScheduler(database, sp.GetRequiredService<ILogger<ScanScheduler>>()));

        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanScheduler>());

        var app = builder.Build();

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = HandleException,
            AllowStatusCode404Response = true,
        });

        // A request body that will not parse never reaches the handler and never throws: minimal
        // API binding writes a bare 400 and stops. This fills in the body, so someone writing a
        // client gets a sentence rather than a blank response.
        app.UseStatusCodePages(async context =>
            await context.HttpContext.Response
                .WriteAsJsonAsync(new ApiError(Explain(context.HttpContext.Response.StatusCode)))
                .ConfigureAwait(false));

        if (options.LogRequests)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Strata.Api");

            app.Use(async (context, next) =>
            {
                var clock = Stopwatch.StartNew();

                try
                {
                    await next(context).ConfigureAwait(false);
                }
                finally
                {
                    clock.Stop();

                    var path = context.Request.Path.Value ?? "";
                    var query = context.Request.QueryString.Value ?? "";

                    Served(
                        logger,
                        context.Request.Method,
                        path,
                        query,
                        context.Response.StatusCode,
                        clock.Elapsed.TotalMilliseconds);
                }
            });
        }

        ApiEndpoints.MapAll(app);
        MapUserInterface(app);

        return app;
    }

    /// <summary>Whether a built Angular app was found next to the executable.</summary>
    internal static bool HasUserInterface => Directory.Exists(WebRoot) &&
                                            File.Exists(Path.Combine(WebRoot, "index.html"));

    /// <summary>
    /// Where the built UI lives.
    /// </summary>
    /// <remarks>
    /// Next to the executable rather than under the current directory: the shipped shape is a
    /// scheduled task started at logon, whose working directory is whatever Windows felt like.
    /// </remarks>
    internal static string WebRoot { get; } =
        Path.Combine(AppContext.BaseDirectory, "wwwroot");

    /// <summary>
    /// Serves the compiled Angular app from the same origin as the API.
    /// </summary>
    /// <remarks>
    /// Same origin is doing real work here. The API sends no CORS header at all, so the browser's
    /// own rule is what stops a page from anywhere else reading this machine's disk contents through
    /// localhost — and it only holds because the app is served from here rather than from a dev
    /// server or a file:// page.
    /// <para>
    /// Absent when nobody has run <c>ng build</c>, which is a normal state for a backend-only
    /// checkout. The API still works; the root just says so instead of 404ing at someone.
    /// </para>
    /// </remarks>
    private static void MapUserInterface(WebApplication app)
    {
        if (!HasUserInterface)
        {
            app.MapGet("/", () => Results.Text(
                "Strata is running, but no UI has been built.\n\n" +
                "Build it with:  cd src/Strata.Web && npm ci && npm run build\n" +
                "or:             dotnet build src/Strata.Host -p:BuildWebUi=true\n\n" +
                "The API is at /api. See tools/api-probe.http.\n",
                "text/plain"));

            return;
        }

        var files = new PhysicalFileProvider(WebRoot);

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,

            // Every asset except index.html carries a content hash in its name, so it can be
            // cached hard. index.html is the one file that must not be, or a rebuilt UI would be
            // invisible until someone thought to force-reload.
            OnPrepareResponse = context =>
            {
                var name = context.File.Name;

                context.Context.Response.Headers.CacheControl =
                    name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                        ? "no-cache, no-store, must-revalidate"
                        : "public, max-age=31536000, immutable";
            },
        });

        // The app routes client-side, so /timeline is a real URL that the server has no file for.
        // Anything not under /api and not a file falls back to the app, which then reads the path
        // itself. Scoped away from /api so a mistyped endpoint still answers with the JSON error
        // shape rather than with a page.
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;

                await context.Response.WriteAsJsonAsync(new ApiError("No such endpoint."))
                    .ConfigureAwait(false);

                return;
            }

            context.Response.ContentType = "text/html";
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";

            await context.Response.SendFileAsync(Path.Combine(WebRoot, "index.html"))
                .ConfigureAwait(false);
        });
    }

    private static string Explain(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest =>
            "The request could not be read. Check the JSON body and the query parameters.",
        StatusCodes.Status404NotFound => "No such endpoint.",
        StatusCodes.Status405MethodNotAllowed => "That endpoint does not accept this method.",
        StatusCodes.Status415UnsupportedMediaType => "Send a JSON body with Content-Type: application/json.",
        _ => "The request failed.",
    };

    /// <summary>
    /// Turns an unhandled failure into the same error shape as everything else.
    /// </summary>
    /// <remarks>
    /// A malformed request body is the case that matters. ASP.NET Core answers it with a bare 400
    /// and no content, which is the least helpful possible reply to someone writing a client.
    /// </remarks>
    private static async Task HandleException(HttpContext context)
    {
        var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;

        var (status, message) = failure switch
        {
            BadHttpRequestException bad => (StatusCodes.Status400BadRequest, bad.Message),
            ArgumentException argument => (StatusCodes.Status400BadRequest, argument.Message),
            _ => (StatusCodes.Status500InternalServerError, "Unhandled failure. See the server log."),
        };

        context.Response.StatusCode = status;

        await context.Response.WriteAsJsonAsync(new ApiError(message)).ConfigureAwait(false);
    }

    /// <summary>
    /// One line per request with how long it took.
    /// </summary>
    /// <remarks>
    /// This project's rule is to time queries rather than read their plans, after a lookup that
    /// looked healthy under EXPLAIN QUERY PLAN turned out to cost a factor of 44,000. The same rule
    /// applies to the read side, and this is how the numbers in docs/MEASUREMENTS.md were taken.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{method} {path}{query} -> {status} in {ms:F1} ms")]
    private static partial void Served(
        ILogger logger, string method, string path, string query, int status, double ms);
}
