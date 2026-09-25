export interface ConnectionRepairRequest {
  agentName: string
  url: string
}

const marker = 'CONNECTION REPAIR REQUIRED'
const connectionSettingsPath =
  /^\/environments\/[0-9a-f-]{36}\/bots\/[0-9a-f-]{36}\/settings\/connections\/?$/i

export function parseConnectionRepairRequest(
  answer: string,
): ConnectionRepairRequest | undefined {
  if (!answer.includes(marker)) return undefined

  const agentName = answer.match(/^Connection:\s*(.+)$/im)?.[1]?.trim()
  const rawUrl = answer.match(/^Open connection settings:\s*(https:\/\/\S+)$/im)?.[1]
  if (!agentName || agentName.length > 256 || !rawUrl) return undefined

  try {
    const url = new URL(rawUrl)
    if (
      url.protocol !== 'https:' ||
      url.hostname !== 'copilotstudio.microsoft.com' ||
      url.username ||
      url.password ||
      url.search ||
      url.hash ||
      !connectionSettingsPath.test(url.pathname)
    ) {
      return undefined
    }

    const segments = url.pathname.split('/').filter(Boolean)
    if (!isGuid(segments[1]) || !isGuid(segments[3])) return undefined
    return { agentName, url: url.href }
  } catch {
    return undefined
  }
}

function isGuid(value: string | undefined) {
  return value !== undefined &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
}
