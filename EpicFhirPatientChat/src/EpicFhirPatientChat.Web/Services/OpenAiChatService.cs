using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EpicFhirPatientChat.Web.Models;

namespace EpicFhirPatientChat.Web.Services;

public sealed class OpenAiChatService(HttpClient httpClient, IConfiguration config)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> AskPatientQuestionAsync(PatientContext context, string userQuestion, CancellationToken ct)
    {
        var apiKey = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "OpenAI:ApiKey is not configured. Set it with user-secrets or an environment variable.";
        }

        var model = config["OpenAI:Model"] ?? "gpt-4.1-mini";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            model,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = "You answer questions about a single patient's scrubbed FHIR sandbox data. Use only the supplied context. Do not invent diagnoses, medications, visits, or lab values. If data is missing, say it is not present in the loaded FHIR data. This is not medical advice; recommend clinician verification for clinical decisions."
                },
                new
                {
                    role = "user",
                    content = $"FHIR patient context:\n{context.ToClinicalContextText()}\n\nQuestion: {userQuestion}"
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return $"OpenAI request failed with HTTP {(int)response.StatusCode}: {body}";
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("output_text", out var outputText))
        {
            return outputText.GetString() ?? string.Empty;
        }

        return ExtractTextFallback(doc.RootElement) ?? body;
    }

    private static string? ExtractTextFallback(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text)) builder.AppendLine(text.GetString());
            }
        }
        return builder.Length == 0 ? null : builder.ToString().Trim();
    }
}
