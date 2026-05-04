using EpicFhirPatientChat.Web.Models;
using EpicFhirPatientChat.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".EpicFhirPatientChat.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.IdleTimeout = TimeSpan.FromMinutes(60);
});
builder.Services.AddHttpContextAccessor();

builder.Services.AddHttpClient<EpicFhirClient>();
builder.Services.AddHttpClient<OpenAiChatService>();
builder.Services.AddHttpClient<SmartAuthService>();
builder.Services.AddSingleton<PatientDataScrubber>();
builder.Services.AddSingleton<PatientContextStore>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/smart/launch", async (string? iss, string? launch, SmartAuthService smart, CancellationToken ct) =>
{
    try
    {
        var authorizeUrl = await smart.BuildAuthorizeUrlAsync(iss, launch, ct);
        return Results.Redirect(authorizeUrl);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/smart/login", async (SmartAuthService smart, CancellationToken ct) =>
{
    try
    {
        var authorizeUrl = await smart.BuildAuthorizeUrlAsync(iss: null, launch: null, ct);
        return Results.Redirect(authorizeUrl);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/smart/callback", async (string? code, string? state, string? error, string? error_description, SmartAuthService smart, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        return Results.BadRequest(new { error, error_description });
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest(new { error = "Callback requires code and state." });
    }

    try
    {
        await smart.ExchangeCodeAsync(code, state, ct);
        return Results.Redirect("/?smart=connected");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/smart/status", (SmartAuthService smart) => Results.Ok(smart.GetStatus()));
app.MapPost("/api/smart/logout", (SmartAuthService smart) =>
{
    smart.Logout();
    return Results.Ok(new { authenticated = false });
});

app.MapPost("/api/patient/load", async (
    LoadPatientRequest request,
    IConfiguration config,
    IHttpContextAccessor accessor,
    EpicFhirClient fhirClient,
    PatientDataScrubber scrubber,
    PatientContextStore store,
    CancellationToken ct) =>
{
    var smartPatientId = accessor.HttpContext?.Session.GetString("smart:patient");
    var patientId = string.IsNullOrWhiteSpace(request.PatientId)
        ? smartPatientId ?? config["Epic:DefaultPatientId"]
        : request.PatientId;

    if (string.IsNullOrWhiteSpace(patientId))
    {
        return Results.BadRequest(new { error = "Patient id is required. For SMART launch, make sure the token response included patient context." });
    }

    var rawBundle = await fhirClient.GetPatientVisitDataAsync(patientId, ct);
    var context = scrubber.BuildContext(patientId, rawBundle);
    store.Set(patientId, context);

    return Results.Ok(new LoadPatientResponse(patientId, context));
});

app.MapPost("/api/chat", async (
    ChatRequest request,
    PatientContextStore store,
    OpenAiChatService chat,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.PatientId) || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Patient id and message are required." });
    }

    var context = store.Get(request.PatientId);
    if (context is null)
    {
        return Results.BadRequest(new { error = "Load patient data before chatting." });
    }

    var answer = await chat.AskPatientQuestionAsync(context, request.Message, ct);
    return Results.Ok(new ChatResponse(answer));
});

app.Run();
