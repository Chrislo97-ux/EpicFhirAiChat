using System.Collections.Concurrent;
using EpicFhirPatientChat.Web.Models;

namespace EpicFhirPatientChat.Web.Services;

public sealed class PatientContextStore
{
    private readonly ConcurrentDictionary<string, PatientContext> _store = new();
    public void Set(string patientId, PatientContext context) => _store[patientId] = context;
    public PatientContext? Get(string patientId) => _store.TryGetValue(patientId, out var context) ? context : null;
}
