import { createElement, type ReactNode } from 'react'
import { renderPlanTimeline } from '../../FoundryCopilotA2A.BrowserShared/planView.ts'
import type { PlanUpdate } from '../../FoundryCopilotA2A.BrowserShared/planUpdates.ts'
import '../../FoundryCopilotA2A.BrowserShared/plans.css'

export default function PlanTimeline({ updates, finished }: { updates?: PlanUpdate[]; finished: boolean }) {
  return renderPlanTimeline<ReactNode>(createElement, updates, finished)
}
