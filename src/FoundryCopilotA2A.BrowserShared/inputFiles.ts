export const maxFileBytes = 5 * 1024 * 1024
export const fileTypes: Record<string, string> = {
  txt: 'text/plain', csv: 'text/csv', pdf: 'application/pdf',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg',
}
export const fileAccept = Object.keys(fileTypes).map((extension) => `.${extension}`).join(',')

export interface InputFileMetadata {
  name: string
  mediaType: string
  size: number
}

export interface InputFile extends InputFileMetadata {
  bytes: string
}

export function validateFile(file: InputFileMetadata) {
  const extension = file.name.split('.').at(-1)?.toLowerCase() ?? ''
  if (!file.name || file.name.length > 180 || /[/\\:\u0000-\u001f\u007f-\u009f]/.test(file.name)) {
    throw new Error('Use a filename of 1-180 characters without paths or control characters.')
  }
  if (!fileTypes[extension] || file.mediaType !== fileTypes[extension]) {
    throw new Error('Choose a TXT, CSV, PDF, DOCX, PNG or JPEG file.')
  }
  if (!Number.isInteger(file.size) || file.size < 1 || file.size > maxFileBytes) {
    throw new Error('Choose a nonempty file no larger than 5 MB.')
  }
}

export async function readInputFile(file: File): Promise<InputFile> {
  const mediaType = fileTypes[file.name.split('.').at(-1)?.toLowerCase() ?? ''] ?? ''
  const metadata = { name: file.name, mediaType, size: file.size }
  validateFile(metadata)
  const buffer = new Uint8Array(await file.arrayBuffer())
  let binary = ''
  for (let offset = 0; offset < buffer.length; offset += 8192) {
    binary += String.fromCharCode(...buffer.subarray(offset, offset + 8192))
  }
  return { ...metadata, bytes: btoa(binary) }
}

export function fileSizeLabel(bytes: number) {
  return bytes < 1024 * 1024 ? `${Math.max(1, Math.ceil(bytes / 1024))} KB` :
    `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
