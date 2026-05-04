using System.Net.Http.Headers;
using System.Text.Json;

namespace EpicFhirPatientChat.Web.Services;

public sealed class EpicFhirClient(HttpClient httpClient, IConfiguration config, IHttpContextAccessor accessor, ILogger<EpicFhirClient> logger)
{
    public async Task<IReadOnlyDictionary<string, JsonDocument?>> GetPatientVisitDataAsync(string patientId, CancellationToken ct)
    {
        var resources = new Dictionary<string, JsonDocument?>
        {
            ["Patient"] = await GetJsonOrNullAsync($"Patient/{Uri.EscapeDataString(patientId)}", ct),
            ["Encounter"] = await GetJsonOrNullAsync($"Encounter?patient={Uri.EscapeDataString(patientId)}", ct),
            ["Condition"] = await GetJsonOrNullAsync($"Condition?patient={Uri.EscapeDataString(patientId)}", ct),
            ["ObservationLabs"] = await GetJsonOrNullAsync($"Observation?patient={Uri.EscapeDataString(patientId)}&category=laboratory", ct),
            ["ObservationVitals"] = await GetJsonOrNullAsync($"Observation?patient={Uri.EscapeDataString(patientId)}&category=vital-signs", ct),
            ["MedicationRequest"] = await GetJsonOrNullAsync($"MedicationRequest?patient={Uri.EscapeDataString(patientId)}", ct),
            ["AllergyIntolerance"] = await GetJsonOrNullAsync($"AllergyIntolerance?patient={Uri.EscapeDataString(patientId)}", ct)
        };

        return resources;
    }

    private async Task<JsonDocument?> GetJsonOrNullAsync(string relativeUrl, CancellationToken ct)
    {
        var baseUrl = accessor.HttpContext?.Session.GetString("smart:fhirBaseUrl") ?? config["Epic:FhirBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("Epic:FhirBaseUrl is not configured and no SMART session FHIR base URL is available.");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), relativeUrl));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        var token = accessor.HttpContext?.Session.GetString("smart:accessToken") ?? config["Epic:BearerToken"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("FHIR request failed for {RelativeUrl} with HTTP {StatusCode}: {Body}", relativeUrl, (int)response.StatusCode, body);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
