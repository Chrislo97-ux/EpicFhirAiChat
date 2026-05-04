namespace EpicFhirPatientChat.Web.Models;

public sealed record LoadPatientRequest(string? PatientId);
public sealed record LoadPatientResponse(string PatientId, PatientContext Context);
public sealed record ChatRequest(string PatientId, string Message);
public sealed record ChatResponse(string Answer);

public sealed record PatientContext(
    string PatientKey,
    string? DisplayName,
    string? BirthYear,
    string? AdministrativeGender,
    IReadOnlyList<VisitSummary> Visits,
    IReadOnlyList<ClinicalFact> Conditions,
    IReadOnlyList<ClinicalFact> Observations,
    IReadOnlyList<ClinicalFact> Medications,
    IReadOnlyList<ClinicalFact> Allergies)
{
    public string ToClinicalContextText()
    {
        static string JoinFacts(IEnumerable<ClinicalFact> facts) =>
            string.Join("\n", facts.Select(f => $"- {f.Name}; value/status: {f.ValueOrStatus}; date: {f.Date}; source: {f.SourceResourceType}/{f.SourceId}"));

        var visits = string.Join("\n", Visits.Select(v => $"- {v.Type}; status: {v.Status}; class: {v.Class}; period: {v.Period}; reason: {v.Reason}; source: Encounter/{v.SourceId}"));

        return $$"""
        Patient summary:
        - Patient key: {{PatientKey}}
        - Display name: {{DisplayName ?? "not supplied"}}
        - Birth year: {{BirthYear ?? "not supplied"}}
        - Gender: {{AdministrativeGender ?? "not supplied"}}

        Visits / encounters:
        {{(string.IsNullOrWhiteSpace(visits) ? "- none found" : visits)}}

        Conditions:
        {{(Conditions.Count == 0 ? "- none found" : JoinFacts(Conditions))}}

        Observations / labs / vitals:
        {{(Observations.Count == 0 ? "- none found" : JoinFacts(Observations))}}

        Medications:
        {{(Medications.Count == 0 ? "- none found" : JoinFacts(Medications))}}

        Allergies:
        {{(Allergies.Count == 0 ? "- none found" : JoinFacts(Allergies))}}
        """;
    }
}

public sealed record VisitSummary(
    string? Type,
    string? Status,
    string? Class,
    string? Period,
    string? Reason,
    string? SourceId);

public sealed record ClinicalFact(
    string Name,
    string? ValueOrStatus,
    string? Date,
    string SourceResourceType,
    string? SourceId);
