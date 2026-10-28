const responderHeader = /^Responding agent:[ \t]+[^\r\n]+(?:\r?\n[ \t]*)*\r?\n/

/**
 * Turns an agent progress update into one display line: leading responder headers are dropped,
 * whitespace collapses, and a trailing "..." becomes a nonbreaking space plus an ellipsis.
 */
export function activityLine(update: string | undefined): string | undefined {
  if (!update) return undefined
  let text = update
  for (let header = responderHeader.exec(text); header; header = responderHeader.exec(text)) {
    text = text.slice(header[0].length)
  }
  text = text.replace(/\s+/g, ' ').trim()
  if (!text) return undefined
  return text.replace(/\s*(?:\.\.\.|…)$/, '\u00a0…')
}

/** Display lines for a turn's saved updates, without empties or consecutive repeats. */
export function activityLines(updates: readonly string[] | undefined): string[] {
  const lines: string[] = []
  for (const update of updates ?? []) {
    const line = activityLine(update)
    if (line && line !== lines.at(-1)) lines.push(line)
  }
  return lines
}
