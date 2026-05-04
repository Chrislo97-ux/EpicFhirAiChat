const patientInput = document.getElementById('patientId');
const loadBtn = document.getElementById('loadBtn');
const summary = document.getElementById('summary');
const form = document.getElementById('chatForm');
const messageInput = document.getElementById('message');
const messages = document.getElementById('messages');
const smartLoginBtn = document.getElementById('smartLoginBtn');
const smartLogoutBtn = document.getElementById('smartLogoutBtn');
const smartStatus = document.getElementById('smartStatus');

let loadedPatientId = null;

function addMessage(role, text) {
  const div = document.createElement('div');
  div.className = `message ${role}`;
  div.textContent = text;
  messages.appendChild(div);
  messages.scrollTop = messages.scrollHeight;
}

async function refreshSmartStatus() {
  try {
    const response = await fetch('/api/smart/status');
    const status = await response.json();
    if (status.authenticated) {
      smartStatus.textContent = `SMART connected${status.patientId ? `; patient context: ${status.patientId}` : ''}`;
      if (status.patientId) patientInput.value = status.patientId;
    } else {
      smartStatus.textContent = 'SMART not connected. Manual sandbox patient id can still be used if endpoint access allows it.';
    }
  } catch (err) {
    smartStatus.textContent = `SMART status error: ${err.message}`;
  }
}

smartLoginBtn.addEventListener('click', () => {
  window.location.href = '/smart/login';
});

smartLogoutBtn.addEventListener('click', async () => {
  await fetch('/api/smart/logout', { method: 'POST' });
  loadedPatientId = null;
  await refreshSmartStatus();
  addMessage('assistant', 'SMART session cleared.');
});

loadBtn.addEventListener('click', async () => {
  loadBtn.disabled = true;
  summary.textContent = 'Loading Epic FHIR resources...';
  try {
    const response = await fetch('/api/patient/load', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ patientId: patientInput.value })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || 'Load failed');
    loadedPatientId = result.patientId;
    const c = result.context;
    summary.textContent = JSON.stringify({
      patientKey: c.patientKey,
      displayName: c.displayName,
      birthYear: c.birthYear,
      gender: c.administrativeGender,
      visitCount: c.visits.length,
      conditionCount: c.conditions.length,
      observationCount: c.observations.length,
      medicationCount: c.medications.length,
      allergyCount: c.allergies.length
    }, null, 2);
    addMessage('assistant', 'Patient data loaded and scrubbed. What would you like to know?');
  } catch (err) {
    summary.textContent = err.message;
  } finally {
    loadBtn.disabled = false;
  }
});

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const text = messageInput.value.trim();
  if (!text) return;
  if (!loadedPatientId) {
    addMessage('assistant', 'Load patient data first.');
    return;
  }
  addMessage('user', text);
  messageInput.value = '';
  const pending = document.createElement('div');
  pending.className = 'message assistant';
  pending.textContent = 'Thinking...';
  messages.appendChild(pending);

  try {
    const response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ patientId: loadedPatientId, message: text })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || 'Chat failed');
    pending.textContent = result.answer;
  } catch (err) {
    pending.textContent = err.message;
  }
});

refreshSmartStatus();
