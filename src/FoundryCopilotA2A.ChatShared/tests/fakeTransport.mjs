// An in-memory transport and storage that let shared session tests run without any backend.

export function memoryStorage() {
  const entries = new Map()
  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => entries.set(key, value),
    removeItem: (key) => entries.delete(key),
  }
}

/**
 * Each send is answered by `reply(request)`, which may stream through `request.update` and
 * returns the outcome. Requests are recorded so tests can inspect what the session supplied.
 */
export function fakeTransport(reply = async ({ text }) => ({ turn: { answer: `Echo: ${text}` } }), options = {}) {
  const requests = []
  return {
    requests,
    discardsPartialAnswer: options.discardsPartialAnswer,
    prepare(input = {}) {
      if (input.invalid) throw new Error('Invalid input.')
      const files = input.file ? [{ name: input.file, mediaType: 'text/plain', size: 5 }] : []
      return { hasAttachment: files.length > 0, files, ...(input.responder ? { responder: input.responder } : {}) }
    },
    async send(request) {
      requests.push(request)
      return reply(request)
    },
  }
}

/** A promise with its resolve/reject exposed, to hold a reply open while a test acts. */
export function deferred() {
  let resolve
  let reject
  const promise = new Promise((done, fail) => { resolve = done; reject = fail })
  return { promise, resolve, reject }
}

/** Codec for a test turn type with one app-specific `note` field. */
export const noteCodec = {
  validateFile(file) {
    if (file.size > 100) throw new Error('Saved file exceeds the test limit.')
  },
  readExtras: (saved, status) => status === 'succeeded' && typeof saved.note === 'string' ? { note: saved.note } : {},
  writeExtras: (turn) => ({ note: turn.note }),
}
