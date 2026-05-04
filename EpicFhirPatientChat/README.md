# Epic FHIR Patient Chat (.NET 8)

A ASP.NET Core .NET 8 POC sample that:

1. Connects to Epic's sandbox through SMART on FHIR OAuth authorization-code flow.
2. Pulls patient visit-related data from Epic FHIR R4 endpoints.
3. Normalizes and **scrubs/minimizes** FHIR JSON into a compact patient-context document.
4. Provides a browser chat page for asking questions about the patient's data.
5. Uses ChatGPT through the OpenAI API.

> This is a developer sandbox proof-of-concept. Do not use it with production PHI without adding your organization's security, consent, logging, data-retention, HIPAA/BAA, and EHR app-review requirements.


## Architecture

```text
Browser chat UI
   |
ASP.NET Core Minimal API (.NET 8)
   |-- SMART OAuth endpoints: /smart/login, /smart/launch, /smart/callback
   |-- SmartAuthService: discovers authorize/token URLs, creates state + PKCE, exchanges code for token
   |-- EpicFhirClient: reads Patient, Encounter, Observation, Condition, MedicationRequest, AllergyIntolerance with bearer token
   |-- PatientDataScrubber: keeps clinically useful fields and removes/minimizes raw identifiers
   |-- PatientContextStore: in-memory cache by patient id
   |-- OpenAiChatService: sends scrubbed patient context + question to ChatGPT
   |
Epic FHIR R4 Sandbox + OpenAI API
```

## Prerequisites

- .NET 8 SDK
- An OpenAI API key
- An Epic sandbox SMART on FHIR app registration
  --If you don't know how to  register an application with the Epic sandbox, google it.  You'll need to do this in order to get a ClientID and ClientSecret (needed for access tokens in order to access the FHIR endpoints

## Epic app registration values

When registering your Epic sandbox app, configure the redirect URI to match your local app URL:

```text
https://localhost:5001/smart/callback
```

If your local Kestrel port is different, update both Epic's app registration and `Epic:RedirectUri`.

## Configuration

Set secrets with environment variables or user-secrets:

```bash
cd src/EpicFhirPatientChat.Web

dotnet user-secrets init

dotnet user-secrets set "OpenAI:ApiKey" "YOUR_OPENAI_API_KEY"
dotnet user-secrets set "Epic:ClientId" "YOUR_EPIC_CLIENT_ID"
dotnet user-secrets set "Epic:RedirectUri" "https://localhost:5001/smart/callback"

# Only if Epic issues your sandbox app a confidential-client secret:
dotnet user-secrets set "Epic:ClientSecret" "YOUR_EPIC_CLIENT_SECRET"
```

The default Epic R4 base URL is:

```text
https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/
```

The app tries SMART discovery first:

1. `{iss}/.well-known/smart-configuration`
2. `{iss}/metadata`
3. Explicit `Epic:AuthorizeUrl` and `Epic:TokenUrl`, if configured

## Default scopes

```text
launch/patient patient/*.read openid fhirUser profile offline_access
```

You can change these in `appsettings.json` or user-secrets:

```bash
dotnet user-secrets set "Epic:Scopes" "launch/patient patient/Patient.read patient/Encounter.read patient/Observation.read patient/Condition.read patient/MedicationRequest.read patient/AllergyIntolerance.read openid fhirUser profile offline_access"
```

## Run

```bash
cd src/EpicFhirPatientChat.Web
dotnet restore
dotnet run
```

Open the HTTPS URL shown by Kestrel. For Epic OAuth callbacks, the URL must match `Epic:RedirectUri`.

## How to use

### Standalone SMART sandbox login

1. Click **Connect to Epic SMART sandbox**.
2. Complete Epic's sandbox authorization flow.
3. Return to the app.
4. Click **Load patient data from Epic sandbox**.
5. Ask questions like:
   - “Summarize the patient’s recent visits.”
   - “What abnormal lab results are present?”
   - “What active problems or medications are visible?”

### EHR launch flow

Use this launch URL from Epic's launchpad or simulator:

```text
https://localhost:5001/smart/launch
```

Epic should call it with query parameters like:

```text
/smart/launch?iss={FHIR_BASE_URL}&launch={LAUNCH_TOKEN}
```

The app stores the SMART access token and patient context in the ASP.NET Core session.

## Important implementation notes

- Uses OAuth authorization-code flow with PKCE (`S256`).
- Uses `state` validation to protect the callback.
- Uses server-side ASP.NET Core session storage for token demo purposes.
- For production, replace in-memory session/cache with encrypted distributed storage, add refresh-token handling, validate ID tokens if used, and implement audit/security controls.
- The chat prompt instructs the LLM to answer only from scrubbed FHIR context and not invent clinical facts.

## Project layout

```text
EpicFhirPatientChat.sln
src/EpicFhirPatientChat.Web/
  Program.cs
  appsettings.json
  Models/AppModels.cs
  Services/
    EpicFhirClient.cs
    OpenAiChatService.cs
    PatientContextStore.cs
    PatientDataScrubber.cs
    SmartAuthService.cs
  wwwroot/
    index.html
    app.js
    styles.css
```
