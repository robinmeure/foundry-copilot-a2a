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
  sparkle: 'm12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5L12 3Z',
  shield: 'm12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3Zm-4 9 3 3 5-6',
}

export default function ChatIcon({ name }: { name: keyof typeof paths }) {
  return (
    <svg className="chat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={paths[name]} />
    </svg>
  )
}
