/** Only leading, standalone responder paragraphs become presentation-only attribution. */
export function prepareResponseAttribution(answer: string, speaker: string, isStreaming: boolean) {
  let text = answer
  const contributors = new Set<string>()
  while (true) {
    const header = /^Responding agent:[ \t]+([^\r\n]+)(?:\r?\n[ \t]*\r?\n|$)/.exec(text)
    if (!header || (isStreaming && !header[0].endsWith('\n'))) break
    const name = header[1].trim()
    if (!name) break
    if (name !== speaker) contributors.add(name)
    text = text.slice(header[0].length)
  }
  return { text, contributors: [...contributors] }
}
