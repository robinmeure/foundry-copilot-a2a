export function parseConsentRequest(answer: string): { url: string } | undefined {
  if (
    !answer.includes('AUTHENTICATION REQUIRED:') ||
    !answer.toLowerCase().includes('user consent is required')
  ) {
    return undefined
  }

  const match = answer.match(/https:\/\/[^\s<>"']+/i)
  if (!match) {
    return undefined
  }

  try {
    const url = new URL(match[0])
    const isAzureApimConsentHost =
      url.hostname === 'consent.azure-apim.net' ||
      url.hostname.endsWith('.consent.azure-apim.net')
    return url.protocol === 'https:' && isAzureApimConsentHost
      ? { url: url.href }
      : undefined
  } catch {
    return undefined
  }
}
