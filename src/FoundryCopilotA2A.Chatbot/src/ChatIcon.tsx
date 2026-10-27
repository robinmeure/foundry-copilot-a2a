const paths = {
  'new-chat': 'M12 5v14M5 12h14',
  help: 'M9.1 9a3 3 0 0 1 5.8 1c0 2-3 2-3 4m.1 3h.01M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z',
  'sign-out': 'M9 5H5v14h4m6-14 7 7-7 7m-5-7h12',
  'arrow-up': 'M12 20V4m-6 6 6-6 6 6',
  'arrow-down': 'M12 4v16m-6-6 6 6 6-6',
  stop: 'M6 6h12v12H6Z',
  copy: 'M9 9h11v12H9ZM15 5V2H3v14h3',
  link: 'm10 13 4-4m-6 6-1 1a4 4 0 0 1-6-6l4-4a4 4 0 0 1 6 0m2 3 1-1a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-6 0',
  'chevron-down': 'm7 10 5 5 5-5',
  'chevron-up': 'm7 14 5-5 5 5',
  check: 'm5 12 4 4L19 6',
  mic: 'M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3Zm-7 9a7 7 0 0 0 14 0m-7 7v3',
  attach: 'm8 12 6-6a3 3 0 0 1 4 4l-8 8a5 5 0 0 1-7-7l9-9m-7 12 8-8',
  file: 'M14 2H5v20h14V7l-5-5Zm0 0v5h5M8 12h8m-8 4h6',
  folder: 'M3 6h7l2 3h9v11H3V6Zm0 0V4h7l2 2h7v3',
  sharepoint: 'M8 4h12v12H8V4ZM4 8h12v12H4V8Zm4 4h4m-4 4h4',
  search: 'M16 16l5 5M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z',
  close: 'm6 6 12 12M6 18 18 6',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 14h10l1-14M10 11v6m4-6v6',
  sparkle: 'm12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5L12 3Z',
  shield: 'm12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3Zm-4 9 3 3 5-6',
  settings: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm7.4-2.5a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3h.1a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8v.1a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z',
}

export default function ChatIcon({ name }: { name: keyof typeof paths }) {
  return (
    <svg className="chat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={paths[name]} />
    </svg>
  )
}
