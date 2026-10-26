import { planEventKey, planEventLabel, planGroups, type PlanUpdate } from './planUpdates.ts'

type ElementFactory<T> = (tag: string, props: Record<string, unknown> | null, ...children: (T | string | null)[]) => T

export function renderPlanTimeline<T>(
  h: ElementFactory<T>, events: readonly PlanUpdate[] | undefined, finished: boolean,
): T | null {
  if (!events?.length) return null
  return h('section', { className: 'a2a-plan-timeline', 'aria-label': 'Agent plan activity' },
    h('span', { className: 'a2a-plan-announcement', role: 'status', 'aria-live': 'polite', 'aria-atomic': true },
      finished ? 'Plan activity is available.' : planEventLabel(events.at(-1)!)),
    ...planGroups(events).map(({ key, updates }, index) => {
      const steps = new Map<string, PlanUpdate>()
      for (const event of updates) {
        if (event.name !== 'DynamicPlanReceived' && event.value.stepId) steps.set(event.value.stepId, event)
      }
      const completed = [...steps.values()].filter(step => step.name === 'DynamicPlanStepFinished').length
      return h('details', { key, className: 'a2a-plan', open: true },
        h('summary', null,
          h('span', { className: 'a2a-plan-title' }, `${updates[0].originatingAgent.name} activity`),
          h('span', { className: 'a2a-plan-count' },
            `${updates.length} ${updates.length === 1 ? 'update' : 'updates'}${steps.size ? ` · ${completed}/${steps.size} steps finished` : ''}`)),
        h('span', { className: 'a2a-plan-announcement' }, `Plan ${index + 1}. Step status is separate from the overall response.`),
        h('ol', { className: 'a2a-plan-events' }, ...updates.map(event => {
          const details = Object.fromEntries(Object.entries(event.value)
            .filter(([key]) => ['taskDialogId', 'state', 'steps', 'arguments', 'observation', 'autoFilledArguments'].includes(key)))
          const label = planEventLabel(event)
          return h('li', { key: planEventKey(event), className: 'a2a-plan-event' },
            Object.keys(details).length > 0 || event.detailsFiltered
              ? h('details', { className: 'a2a-plan-details' },
                h('summary', { title: 'Expand activity details' },
                  h('span', { className: 'a2a-plan-label' }, label),
                  event.detailsFiltered ? h('span', { className: 'a2a-plan-filtered' }, 'Filtered') : null),
                h('div', { className: 'a2a-plan-detail-body' },
                  event.detailsFiltered
                    ? h('span', { className: 'a2a-plan-note' }, 'Some fields were omitted, redacted or shortened.') : null,
                  Object.keys(details).length > 0
                    ? h('pre', { tabIndex: 0, role: 'region', 'aria-label': `${label}: sanitized details` },
                      JSON.stringify(details, null, 2)) : null))
              : h('span', { className: 'a2a-plan-label a2a-plan-plain' }, label))
        })))
    }))
}
