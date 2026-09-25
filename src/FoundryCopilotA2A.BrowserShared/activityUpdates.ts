export const maximumActivityUpdates = 12
const maximumActivityUpdateCharacters = 1_200

export function appendActivityUpdate(
  current: readonly string[] | undefined,
  message: string,
): string[] {
  const trimmed = message.trim()
  if (!trimmed) return current ? [...current] : []

  const bounded = trimmed.length <= maximumActivityUpdateCharacters
    ? trimmed
    : `${trimmed.slice(0, maximumActivityUpdateCharacters)}...`
  if (current?.at(-1) === bounded) return [...current]

  return [...(current ?? []), bounded].slice(-maximumActivityUpdates)
}
