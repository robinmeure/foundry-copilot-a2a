/** Word and PowerPoint files the Knowledge API generated for a turn. They live on the server only until they expire. */
export const GENERATED_FILE_TYPES = {
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': { extension: 'docx', label: 'Word document' },
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': { extension: 'pptx', label: 'PowerPoint presentation' },
} as const
export type GeneratedFileMediaType = keyof typeof GENERATED_FILE_TYPES

export const GENERATED_FILE_ID = /^[0-9a-f]{32}$/
export const MAX_GENERATED_FILES_PER_MESSAGE = 2
const MAX_GENERATED_FILE_SIZE = 4 * 1024 * 1024

export interface GeneratedFile {
  id: string
  name: string
  mediaType: GeneratedFileMediaType
  size: number
  expiresAt: string
}

function record(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined
}

export function parseGeneratedFile(value: unknown): GeneratedFile | undefined {
  const source = record(value)
  if (!source || typeof source.id !== 'string' || !GENERATED_FILE_ID.test(source.id)) return undefined
  const mediaType = Object.keys(GENERATED_FILE_TYPES).find(type => type === source.mediaType) as GeneratedFileMediaType | undefined
  const { name, size } = source
  const expires = typeof source.expiresAt === 'string' ? Date.parse(source.expiresAt) : Number.NaN
  if (!mediaType || typeof name !== 'string' || name.length > 120 || /[/\\:*?"<>|]/.test(name) ||
      [...name].some(char => char.charCodeAt(0) < 32) ||
      !name.toLowerCase().endsWith(`.${GENERATED_FILE_TYPES[mediaType].extension}`) ||
      typeof size !== 'number' || !Number.isInteger(size) || size < 1 || size > MAX_GENERATED_FILE_SIZE ||
      !Number.isFinite(expires)) return undefined
  return { id: source.id, name, mediaType, size, expiresAt: new Date(expires).toISOString() }
}

/** Drops malformed or duplicate entries instead of failing the whole chat response or saved history. */
export function parseGeneratedFiles(value: unknown): GeneratedFile[] {
  if (!Array.isArray(value)) return []
  const seen = new Set<string>()
  const files: GeneratedFile[] = []
  for (const entry of value) {
    const file = parseGeneratedFile(entry)
    if (!file || seen.has(file.id)) continue
    seen.add(file.id)
    files.push(file)
    if (files.length === MAX_GENERATED_FILES_PER_MESSAGE) break
  }
  return files
}

export function isGeneratedFileExpired(file: Pick<GeneratedFile, 'expiresAt'>, now = Date.now()) {
  const expires = Date.parse(file.expiresAt)
  return !Number.isFinite(expires) || expires <= now
}

export function generatedFileLabel(file: Pick<GeneratedFile, 'mediaType'>) {
  return GENERATED_FILE_TYPES[file.mediaType].label
}

export function formatFileSize(size: number) {
  return size < 1024 ? `${size} B` : size < 1024 * 1024 ? `${Math.round(size / 1024)} KB` : `${(size / 1024 / 1024).toFixed(1)} MB`
}
