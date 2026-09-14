export function readEndpoint(value: string | undefined, name: string, requireHttps = false) {
  if (!value?.trim()) {
    return undefined
  }

  const message = `${name} must be an absolute ${requireHttps ? 'HTTPS' : 'HTTP(S)'} API base URL without credentials, a query, or a fragment.`
  let endpoint: URL
  try {
    endpoint = new URL(value.trim())
  } catch (reason) {
    if (!(reason instanceof TypeError)) {
      throw reason
    }
    throw new Error(message)
  }
  if (
    !['http:', 'https:'].includes(endpoint.protocol) ||
    (requireHttps && endpoint.protocol !== 'https:') ||
    endpoint.username ||
    endpoint.password ||
    endpoint.href.includes('?') ||
    endpoint.href.includes('#')
  ) {
    throw new Error(message)
  }
  return endpoint.href.replace(/\/+$/, '')
}
