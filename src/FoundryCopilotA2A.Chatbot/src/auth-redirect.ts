import { broadcastResponseToMainFrame } from '@azure/msal-browser/redirect-bridge'

try {
  await broadcastResponseToMainFrame()
} catch (reason) {
  const status = document.getElementById('auth-status')!
  status.setAttribute('role', 'alert')
  status.textContent = reason instanceof Error
    ? `Unable to complete sign-in: ${reason.message}`
    : 'Unable to complete sign-in. Return to the chatbot and sign in again.'
}
