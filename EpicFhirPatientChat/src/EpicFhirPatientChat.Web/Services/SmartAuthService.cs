using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EpicFhirPatientChat.Web.Services;

public sealed class SmartAuthService(HttpClient httpClient, IConfiguration config, IHttpContextAccessor accessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> BuildAuthorizeUrlAsync(string? iss, string? launch, CancellationToken ct)
    {
        var session = accessor.HttpContext?.Session ?? throw new InvalidOperationException("Session is not available.");
        var fhirBaseUrl = NormalizeBaseUrl(iss ?? config["Epic:FhirBaseUrl"]);
        var metadata = await GetSmartMetadataAsync(fhirBaseUrl, ct);

        var clientId = config["Epic:ClientId"];
        var redirectUri = config["Epic:RedirectUri"];
        var scopes = config["Epic:Scopes"] ?? "launch/patient patient/*.read openid fhirUser profile offline_access";

        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Epic:ClientId is not configured.");
        if (string.IsNullOrWhiteSpace(redirectUri)) throw new InvalidOperationException("Epic:RedirectUri is not configured.");

        var state = CreateRandomUrlSafeString(32);
        var codeVerifier = CreateRandomUrlSafeString(64);
        var codeChallenge = CreateCodeChallenge(codeVerifier);

        session.SetString("smart:fhirBaseUrl", fhirBaseUrl);
        session.SetString("smart:authorizeUrl", metadata.AuthorizationEndpoint);
        session.SetString("smart:tokenUrl", metadata.TokenEndpoint);
        session.SetString("smart:state", state);
        session.SetString("smart:codeVerifier", codeVerifier);
        if (!string.IsNullOrWhiteSpace(launch)) session.SetString("smart:launch", launch);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scopes,
            ["state"] = state,
            ["aud"] = fhirBaseUrl.TrimEnd('/'),
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        if (!string.IsNullOrWhiteSpace(launch)) query["launch"] = launch;

        return metadata.AuthorizationEndpoint + "?" + BuildQuery(query);
    }

    public async Task<SmartTokenResult> ExchangeCodeAsync(string code, string state, CancellationToken ct)
    {
        var session = accessor.HttpContext?.Session ?? throw new InvalidOperationException("Session is not available.");
        var expectedState = session.GetString("smart:state");
        if (string.IsNullOrWhiteSpace(expectedState) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedState), Encoding.UTF8.GetBytes(state)))
        {
            throw new InvalidOperationException("Invalid SMART OAuth state.");
        }

        var tokenUrl = session.GetString("smart:tokenUrl") ?? config["Epic:TokenUrl"];
        var codeVerifier = session.GetString("smart:codeVerifier");
        var redirectUri = config["Epic:RedirectUri"];
        var clientId = config["Epic:ClientId"];
        var clientSecret = config["Epic:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tokenUrl)) throw new InvalidOperationException("Token endpoint was not discovered or configured.");
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Epic:ClientId is not configured.");
        if (string.IsNullOrWhiteSpace(redirectUri)) throw new InvalidOperationException("Epic:RedirectUri is not configured.");
        if (string.IsNullOrWhiteSpace(codeVerifier)) throw new InvalidOperationException("PKCE verifier is missing from session.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier
        };

        if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret;

        using var response = await httpClient.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token exchange failed with HTTP {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = ReadString(root, "access_token") ?? throw new InvalidOperationException("Token response did not include access_token.");
        var patient = ReadString(root, "patient");
        var encounter = ReadString(root, "encounter");
        var scope = ReadString(root, "scope");
        var refreshToken = ReadString(root, "refresh_token");
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : (int?)null;

        session.SetString("smart:accessToken", accessToken);
        if (!string.IsNullOrWhiteSpace(patient)) session.SetString("smart:patient", patient);
        if (!string.IsNullOrWhiteSpace(encounter)) session.SetString("smart:encounter", encounter);
        if (!string.IsNullOrWhiteSpace(scope)) session.SetString("smart:scope", scope);
        if (!string.IsNullOrWhiteSpace(refreshToken)) session.SetString("smart:refreshToken", refreshToken);
        if (expiresIn is not null) session.SetString("smart:expiresAtUtc", DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).ToString("O"));

        return new SmartTokenResult(patient, encounter, scope, expiresIn);
    }

    public SmartSessionStatus GetStatus()
    {
        var session = accessor.HttpContext?.Session;
        if (session is null) return new SmartSessionStatus(false, null, null, null, null);

        return new SmartSessionStatus(
            Authenticated: !string.IsNullOrWhiteSpace(session.GetString("smart:accessToken")),
            PatientId: session.GetString("smart:patient"),
            FhirBaseUrl: session.GetString("smart:fhirBaseUrl") ?? config["Epic:FhirBaseUrl"],
            Scope: session.GetString("smart:scope"),
            ExpiresAtUtc: session.GetString("smart:expiresAtUtc"));
    }

    public void Logout()
    {
        accessor.HttpContext?.Session.Clear();
    }

    private async Task<SmartMetadata> GetSmartMetadataAsync(string fhirBaseUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config["Epic:AuthorizeUrl"]) && !string.IsNullOrWhiteSpace(config["Epic:TokenUrl"]))
        {
            return new SmartMetadata(config["Epic:AuthorizeUrl"]!, config["Epic:TokenUrl"]!);
        }

        var wellKnownUrl = fhirBaseUrl.TrimEnd('/') + "/.well-known/smart-configuration";
        try
        {
            using var response = await httpClient.GetAsync(wellKnownUrl, ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var auth = ReadString(doc.RootElement, "authorization_endpoint");
                var token = ReadString(doc.RootElement, "token_endpoint");
                if (!string.IsNullOrWhiteSpace(auth) && !string.IsNullOrWhiteSpace(token)) return new SmartMetadata(auth, token);
            }
        }
        catch
        {
            // Fall back to CapabilityStatement OAuth extension below.
        }

        var metadataUrl = fhirBaseUrl.TrimEnd('/') + "/metadata";
        using var metadataResponse = await httpClient.GetAsync(metadataUrl, ct);
        var metadataBody = await metadataResponse.Content.ReadAsStringAsync(ct);
        if (!metadataResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to discover SMART metadata from {wellKnownUrl} or {metadataUrl}. Configure Epic:AuthorizeUrl and Epic:TokenUrl explicitly. Metadata HTTP {(int)metadataResponse.StatusCode}: {metadataBody}");
        }

        using var metadataDoc = JsonDocument.Parse(metadataBody);
        var endpoints = FindOAuthUris(metadataDoc.RootElement);
        if (endpoints.AuthorizationEndpoint is null || endpoints.TokenEndpoint is null)
        {
            throw new InvalidOperationException("FHIR metadata did not include SMART authorize/token endpoints. Configure Epic:AuthorizeUrl and Epic:TokenUrl explicitly.");
        }

        return new SmartMetadata(endpoints.AuthorizationEndpoint, endpoints.TokenEndpoint);
    }

    private static (string? AuthorizationEndpoint, string? TokenEndpoint) FindOAuthUris(JsonElement element)
    {
        string? auth = null;
        string? token = null;

        void Walk(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("url", out var urlElement) && current.TryGetProperty("valueUri", out var valueElement))
                {
                    var url = urlElement.GetString();
                    var value = valueElement.GetString();
                    if (url?.EndsWith("authorize", StringComparison.OrdinalIgnoreCase) == true) auth = value;
                    if (url?.EndsWith("token", StringComparison.OrdinalIgnoreCase) == true) token = value;
                }

                foreach (var property in current.EnumerateObject()) Walk(property.Value);
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in current.EnumerateArray()) Walk(child);
            }
        }

        Walk(element);
        return (auth, token);
    }

    private static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("FHIR base URL is missing. Provide iss from Epic launch or configure Epic:FhirBaseUrl.");
        return url.TrimEnd('/') + "/";
    }

    private static string CreateRandomUrlSafeString(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildQuery(Dictionary<string, string?> parameters) =>
        string.Join("&", parameters
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}"));

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private sealed record SmartMetadata(string AuthorizationEndpoint, string TokenEndpoint);
}

public sealed record SmartTokenResult(string? PatientId, string? EncounterId, string? Scope, int? ExpiresInSeconds);
public sealed record SmartSessionStatus(bool Authenticated, string? PatientId, string? FhirBaseUrl, string? Scope, string? ExpiresAtUtc);
