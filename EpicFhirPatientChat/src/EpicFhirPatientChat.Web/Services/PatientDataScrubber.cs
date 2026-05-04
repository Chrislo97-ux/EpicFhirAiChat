using System.Text.Json;
using EpicFhirPatientChat.Web.Models;

namespace EpicFhirPatientChat.Web.Services;

public sealed class PatientDataScrubber
{
    public PatientContext BuildContext(string patientId, IReadOnlyDictionary<string, JsonDocument?> data)
    {
        var patient = data.GetValueOrDefault("Patient")?.RootElement;

        return new PatientContext(
            PatientKey: ShortKey(patientId),
            DisplayName: patient is null ? null : ReadPatientDisplayName(patient.Value),
            BirthYear: patient is null ? null : ReadBirthYear(patient.Value),
            AdministrativeGender: patient is null ? null : ReadString(patient.Value, "gender"),
            Visits: ReadEncounters(data.GetValueOrDefault("Encounter")),
            Conditions: ReadCodeableConceptFacts(data.GetValueOrDefault("Condition"), "Condition", "code", "clinicalStatus", "recordedDate"),
            Observations: ReadObservations(data.GetValueOrDefault("ObservationLabs")).Concat(ReadObservations(data.GetValueOrDefault("ObservationVitals"))).Take(60).ToList(),
            Medications: ReadCodeableConceptFacts(data.GetValueOrDefault("MedicationRequest"), "MedicationRequest", "medicationCodeableConcept", "status", "authoredOn"),
            Allergies: ReadCodeableConceptFacts(data.GetValueOrDefault("AllergyIntolerance"), "AllergyIntolerance", "code", "clinicalStatus", "recordedDate"));
    }

    private static string ShortKey(string patientId) => patientId.Length <= 8 ? patientId : patientId[..8] + "...";

    private static string? ReadPatientDisplayName(JsonElement patient)
    {
        if (!patient.TryGetProperty("name", out var names) || names.ValueKind != JsonValueKind.Array) return null;
        var first = names.EnumerateArray().FirstOrDefault();
        var family = ReadString(first, "family");
        var given = first.TryGetProperty("given", out var givenArray) && givenArray.ValueKind == JsonValueKind.Array
            ? string.Join(" ", givenArray.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;
        return string.Join(" ", new[] { given, family }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string? ReadBirthYear(JsonElement patient)
    {
        var birthDate = ReadString(patient, "birthDate");
        return birthDate?.Length >= 4 ? birthDate[..4] : null;
    }

    private static IReadOnlyList<VisitSummary> ReadEncounters(JsonDocument? doc) =>
        EnumerateBundleResources(doc)
            .Select(e => new VisitSummary(
                Type: ReadCodeableConceptText(e, "type") ?? ReadCodeableConceptText(e, "serviceType"),
                Status: ReadString(e, "status"),
                Class: e.TryGetProperty("class", out var cls) ? ReadString(cls, "code") ?? ReadString(cls, "display") : null,
                Period: e.TryGetProperty("period", out var p) ? $"{ReadString(p, "start")} to {ReadString(p, "end")}" : null,
                Reason: ReadCodeableConceptText(e, "reasonCode"),
                SourceId: ReadString(e, "id")))
            .Take(20)
            .ToList();

    private static IReadOnlyList<ClinicalFact> ReadCodeableConceptFacts(JsonDocument? doc, string resourceType, string codeProperty, string statusProperty, string dateProperty) =>
        EnumerateBundleResources(doc)
            .Select(r => new ClinicalFact(
                Name: ReadCodeableConceptText(r, codeProperty) ?? "Unknown",
                ValueOrStatus: ReadCodeableConceptText(r, statusProperty) ?? ReadString(r, statusProperty),
                Date: ReadString(r, dateProperty),
                SourceResourceType: resourceType,
                SourceId: ReadString(r, "id")))
            .Where(f => f.Name != "Unknown" || !string.IsNullOrWhiteSpace(f.ValueOrStatus))
            .Take(50)
            .ToList();

    private static IReadOnlyList<ClinicalFact> ReadObservations(JsonDocument? doc) =>
        EnumerateBundleResources(doc)
            .Select(r => new ClinicalFact(
                Name: ReadCodeableConceptText(r, "code") ?? "Observation",
                ValueOrStatus: ReadObservationValue(r) ?? ReadString(r, "status"),
                Date: ReadString(r, "effectiveDateTime") ?? ReadString(r, "issued"),
                SourceResourceType: "Observation",
                SourceId: ReadString(r, "id")))
            .Take(60)
            .ToList();

    private static string? ReadObservationValue(JsonElement obs)
    {
        if (obs.TryGetProperty("valueQuantity", out var q))
        {
            var value = q.TryGetProperty("value", out var v) ? v.ToString() : null;
            var unit = ReadString(q, "unit") ?? ReadString(q, "code");
            return string.Join(" ", new[] { value, unit }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        if (obs.TryGetProperty("valueCodeableConcept", out _)) return ReadCodeableConceptText(obs, "valueCodeableConcept");
        if (obs.TryGetProperty("valueString", out var s)) return s.GetString();
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateBundleResources(JsonDocument? doc)
    {
        if (doc is null) yield break;
        var root = doc.RootElement;
        if (root.TryGetProperty("resourceType", out var rt) && rt.GetString() != "Bundle")
        {
            yield return root;
            yield break;
        }
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("resource", out var resource)) yield return resource;
        }
    }

    private static string? ReadCodeableConceptText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var cc)) return null;
        if (cc.ValueKind == JsonValueKind.Array)
        {
            var first = cc.EnumerateArray().FirstOrDefault();
            return ReadCodeableConcept(first);
        }
        return ReadCodeableConcept(cc);
    }

    private static string? ReadCodeableConcept(JsonElement cc)
    {
        var text = ReadString(cc, "text");
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (cc.TryGetProperty("coding", out var coding) && coding.ValueKind == JsonValueKind.Array)
        {
            var first = coding.EnumerateArray().FirstOrDefault();
            return ReadString(first, "display") ?? ReadString(first, "code");
        }
        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
}
