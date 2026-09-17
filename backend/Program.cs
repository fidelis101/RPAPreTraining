using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// OpenAPI document generation (built into .NET 10).
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "UiPath Orchestrator Middleware";
        doc.Info.Version = "v1";
        doc.Info.Description = "Thin proxy over the UiPath Orchestrator OData API.";
        return Task.CompletedTask;
    });
});

var app = builder.Build();
app.UseCors();

// Surface Orchestrator errors to the UI instead of a bare 500.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

// Swagger: JSON at /openapi/v1.json, UI at /swagger
app.MapOpenApi();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/openapi/v1.json", "UiPath Orchestrator Middleware v1");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "UiPath Middleware API";
});

// --- Folders ---------------------------------------------------------------
app.MapGet("/api/folders", async (Orchestrator orc) =>
{
    var json = await orc.GetAsync("/odata/Folders?$top=200", null);
    var items = json?["value"]?.AsArray() ?? new JsonArray();
    var folders = items.Select(f => new
    {
        id = (long?)f!["Id"],
        name = (string?)f["DisplayName"] ?? (string?)f["FullyQualifiedName"],
        path = (string?)f["FullyQualifiedName"]
    });
    return Results.Ok(folders);
})
.WithName("GetFolders")
.WithTags("Folders")
.WithSummary("List Orchestrator folders")
.WithDescription("Folders the external application has been granted access to.");

// --- Processes (releases) available in a folder -----------------------------
app.MapGet("/api/folders/{folderId:long}/processes", async (long folderId, Orchestrator orc) =>
{
    var json = await orc.GetAsync("/odata/Releases?$top=200", folderId);
    var items = json?["value"]?.AsArray() ?? new JsonArray();
    var releases = items.Select(r => new
    {
        key = (string?)r!["Key"],
        name = (string?)r["Name"],
        processKey = (string?)r["ProcessKey"],
        version = (string?)r["ProcessVersion"]
    });
    return Results.Ok(releases);
})
.WithName("GetProcesses")
.WithTags("Processes")
.WithSummary("List processes (releases) in a folder")
.WithDescription("The returned 'key' is the ReleaseKey you pass to the start-job endpoint.");

// --- Jobs in a folder -------------------------------------------------------
app.MapGet("/api/folders/{folderId:long}/jobs", async (long folderId, Orchestrator orc) =>
{
    var json = await orc.GetAsync("/odata/Jobs?$top=50&$orderby=CreationTime desc", folderId);
    var items = json?["value"]?.AsArray() ?? new JsonArray();
    var jobs = items.Select(j => new
    {
        id = (long?)j!["Id"],
        name = (string?)j["ReleaseName"],
        state = (string?)j["State"],
        started = (string?)j["StartTime"],
        ended = (string?)j["EndTime"],
        info = (string?)j["Info"]
    });
    return Results.Ok(jobs);
})
.WithName("GetJobs")
.WithTags("Jobs")
.WithSummary("List the 50 most recent jobs in a folder");

// --- Start a job ------------------------------------------------------------
app.MapPost("/api/folders/{folderId:long}/jobs/start", async (long folderId, StartJobRequest req, Orchestrator orc) =>
{
    var body = new
    {
        startInfo = new
        {
            ReleaseKey = req.ReleaseKey,
            Strategy = "ModernJobsCount",
            JobsCount = 1
        }
    };
    var json = await orc.PostAsync("/odata/Jobs/UiPath.Server.Configuration.OData.StartJobs", folderId, body);
    return Results.Ok(json);
})
.WithName("StartJob")
.WithTags("Jobs")
.WithSummary("Start a job")
.WithDescription("Starts one job for the given ReleaseKey using the ModernJobsCount strategy.");

// --- Stop a job -------------------------------------------------------------
app.MapPost("/api/folders/{folderId:long}/jobs/{jobId:long}/stop", async (long folderId, long jobId, StopJobRequest? req, Orchestrator orc) =>
{
    var strategy = string.IsNullOrWhiteSpace(req?.Strategy) ? "SoftStop" : req!.Strategy;
    await orc.PostAsync($"/odata/Jobs({jobId})/UiPath.Server.Configuration.OData.StopJob", folderId, new { strategy });
    return Results.Ok(new { stopped = jobId, strategy });
})
.WithName("StopJob")
.WithTags("Jobs")
.WithSummary("Stop a running job")
.WithDescription("Strategy is 'SoftStop' (waits for the next Should Stop checkpoint) or 'Kill'.");

// Sanity-check configuration before serving anything.
var baseUrl = app.Configuration["Orchestrator:BaseUrl"] ?? "";
var clientId = app.Configuration["Orchestrator:ClientId"] ?? "";
if (baseUrl.Contains("YOUR_ORG") || clientId.StartsWith("PASTE_"))
    throw new InvalidOperationException(
        "appsettings.json still has placeholder values. Set Orchestrator:BaseUrl, ClientId and ClientSecret.");
if (!baseUrl.TrimEnd('/').EndsWith("orchestrator_"))
    app.Logger.LogWarning(
        "Orchestrator:BaseUrl is '{BaseUrl}'. It normally ends with /orchestrator_ - " +
        "for example https://cloud.uipath.com/myorg/DefaultTenant/orchestrator_", baseUrl);

app.Run();

/// <param name="ReleaseKey">Key of the process to run, from the processes endpoint.</param>
record StartJobRequest(string ReleaseKey);

/// <param name="Strategy">"SoftStop" (default) or "Kill".</param>
record StopJobRequest(string? Strategy);

/// <summary>Thin wrapper around the UiPath Orchestrator OData API.</summary>
class Orchestrator(IHttpClientFactory factory, IConfiguration config)
{
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string BaseUrl => config["Orchestrator:BaseUrl"]!.TrimEnd('/');

    private async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

        await _lock.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

            var http = factory.CreateClient();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = config["Orchestrator:ClientId"]!,
                ["client_secret"] = config["Orchestrator:ClientSecret"]!,
                ["scope"] = config["Orchestrator:Scope"] ?? "OR.Folders OR.Jobs OR.Execution"
            });

            var url = config["Orchestrator:IdentityUrl"] ?? "https://cloud.uipath.com/identity_/connect/token";
            var res = await http.PostAsync(url, form);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"Token request failed ({(int)res.StatusCode}): {text}");

            if (text.TrimStart().StartsWith('<'))
                throw new HttpRequestException(
                    $"The token endpoint ({url}) returned an HTML page instead of JSON. " +
                    "Check Orchestrator:IdentityUrl.");

            var node = JsonNode.Parse(text)!;
            _token = (string?)node["access_token"]
                ?? throw new HttpRequestException($"No access_token in the token response: {text}");
            var expiresIn = (int?)node["expires_in"] ?? 3600;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
            return _token;
        }
        finally { _lock.Release(); }
    }

    private async Task<HttpClient> ClientAsync(long? folderId)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetTokenAsync());
        if (folderId is not null)
            http.DefaultRequestHeaders.Add("X-UIPATH-OrganizationUnitId", folderId.Value.ToString());
        return http;
    }

    public async Task<JsonNode?> GetAsync(string path, long? folderId)
    {
        var http = await ClientAsync(folderId);
        var res = await http.GetAsync(BaseUrl + path);
        return await ReadAsync(res);
    }

    public async Task<JsonNode?> PostAsync(string path, long? folderId, object body)
    {
        var http = await ClientAsync(folderId);
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.PostAsync(BaseUrl + path, content);
        return await ReadAsync(res);
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage res)
    {
        var url = res.RequestMessage?.RequestUri?.ToString() ?? "(unknown url)";
        var text = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Orchestrator returned {(int)res.StatusCode} for {url}: {Snippet(text)}");

        if (string.IsNullOrWhiteSpace(text)) return null;

        // A 200 that isn't JSON means we reached a web page, not the API.
        if (text.TrimStart().StartsWith('<'))
            throw new HttpRequestException(
                $"Expected JSON from {url} but got an HTML page. " +
                "Check Orchestrator:BaseUrl in appsettings.json - it must be " +
                "https://cloud.uipath.com/{org}/{tenant}/orchestrator_ " +
                $"(org and tenant exactly as they appear in your Orchestrator URL). Response began: {Snippet(text)}");

        try { return JsonNode.Parse(text); }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"Could not parse the response from {url}: {ex.Message}. Body: {Snippet(text)}");
        }
    }

    private static string Snippet(string text) =>
        text.Length <= 300 ? text : text[..300] + "...";
}
