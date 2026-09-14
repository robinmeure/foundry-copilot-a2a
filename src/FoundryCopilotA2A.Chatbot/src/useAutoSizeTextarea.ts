import { useLayoutEffect, useRef } from 'react'

function resize(textarea: HTMLTextAreaElement) {
  const scrollTop = textarea.scrollTop
  textarea.style.height = 'auto'
  textarea.style.height = `${textarea.scrollHeight}px`
  textarea.scrollTop = scrollTop
}

export function useAutoSizeTextarea(value: string) {
  const ref = useRef<HTMLTextAreaElement>(null)

  useLayoutEffect(() => {
    if (ref.current) resize(ref.current)
  }, [value])

  useLayoutEffect(() => {
    const textarea = ref.current
    if (!textarea) return
    let width = textarea.getBoundingClientRect().width
    const observer = new ResizeObserver(() => {
      const nextWidth = textarea.getBoundingClientRect().width
      if (nextWidth !== width) {
        width = nextWidth
        resize(textarea)
      }
    })
    observer.observe(textarea)
    return () => observer.disconnect()
  }, [])

  return ref
}
