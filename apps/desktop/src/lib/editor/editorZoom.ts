export const EDITOR_MIN_FONT_SIZE = 10;
export const EDITOR_MAX_FONT_SIZE = 24;
const WHEEL_FONT_ZOOM_SENSITIVITY = 0.0015;
/**
 * Chromium turns a trackpad pinch into wheel events whose scale is
 * `exp(-deltaY / 100)`. Mouse notches stay on the coarser curve above.
 */
const PINCH_WHEEL_ZOOM_SENSITIVITY = 0.01;
const WHEEL_GESTURE_IDLE_MS = 180;
/**
 * Fingers often scroll a little before a pinch is recognized. A real scroll
 * moves farther than that, which is what keeps ctrl-during-inertia from zooming.
 */
const PINCH_PREFIX_DISTANCE = 80;
const PINCH_PREFIX_SCROLL_MS = 200;

type WheelZoomModifierEvent = Pick<WheelEvent, "ctrlKey" | "metaKey" | "timeStamp"> & Partial<Pick<WheelEvent, "deltaX" | "deltaY" | "deltaMode">>;

export interface EditorWheelZoomEvent extends WheelZoomModifierEvent {
  deltaX: number;
  deltaY: number;
  deltaMode: number;
}

function isSyntheticPinchWheel(event: Partial<Pick<WheelEvent, "deltaX" | "deltaY" | "deltaMode">>): boolean {
  if (event.deltaMode !== 0) return false;
  if (typeof event.deltaX !== "number" || typeof event.deltaY !== "number") return false;
  if (Math.abs(event.deltaX) > 0.5) return false;
  const absY = Math.abs(event.deltaY);
  if (!(absY > 0) || absY >= 250) return false;
  // 100 * ln(scale) is fractional. Integer pixel notches (100 / 120) stay on the mouse curve.
  return Math.abs(event.deltaY - Math.round(event.deltaY)) >= 0.001;
}

export function createEditorWheelZoomGestureGuard(idleMs = WHEEL_GESTURE_IDLE_MS) {
  let lastEventTime = Number.NEGATIVE_INFINITY;
  let startedWithModifier = false;
  let plainScrollEvents = 0;
  let plainScrollDistance = 0;
  let plainScrollStartedAt: number | null = null;
  let gestureId = 0;

  return {
    accepts(event: WheelZoomModifierEvent) {
      const hasModifier = event.metaKey || event.ctrlKey;
      const eventTime = Number.isFinite(event.timeStamp) ? event.timeStamp : 0;
      if (eventTime < lastEventTime || eventTime - lastEventTime > idleMs) {
        startedWithModifier = hasModifier;
        plainScrollEvents = 0;
        plainScrollDistance = 0;
        plainScrollStartedAt = null;
        gestureId += 1;
      }
      if (!hasModifier) {
        if (plainScrollEvents === 0) plainScrollStartedAt = eventTime;
        plainScrollEvents += 1;
        plainScrollDistance += Math.abs(event.deltaY ?? 0);
      } else if (!startedWithModifier && isSyntheticPinchWheel(event)) {
        const prefixMs = plainScrollStartedAt === null ? Number.POSITIVE_INFINITY : eventTime - plainScrollStartedAt;
        if (plainScrollEvents > 0 && plainScrollDistance <= PINCH_PREFIX_DISTANCE && prefixMs <= PINCH_PREFIX_SCROLL_MS) {
          startedWithModifier = true;
        }
      }
      lastEventTime = eventTime;
      return startedWithModifier && hasModifier;
    },
    currentGestureId() {
      return gestureId;
    },
    holdAsPlainScroll(timeStamp: number) {
      const eventTime = Number.isFinite(timeStamp) ? timeStamp : 0;
      lastEventTime = eventTime;
      startedWithModifier = false;
      plainScrollEvents = 1;
      plainScrollDistance = PINCH_PREFIX_DISTANCE + 1;
      plainScrollStartedAt = eventTime;
    },
    reset() {
      lastEventTime = Number.NEGATIVE_INFINITY;
      startedWithModifier = false;
      plainScrollEvents = 0;
      plainScrollDistance = 0;
      plainScrollStartedAt = null;
      gestureId += 1;
    },
  };
}

export function clampEditorFontSize(value: number): number {
  const clamped = Math.min(EDITOR_MAX_FONT_SIZE, Math.max(EDITOR_MIN_FONT_SIZE, value));
  return Number(clamped.toFixed(2));
}

function clampEditorFontSizeExact(value: number): number {
  if (!Number.isFinite(value)) return EDITOR_MIN_FONT_SIZE;
  return Math.min(EDITOR_MAX_FONT_SIZE, Math.max(EDITOR_MIN_FONT_SIZE, value));
}

export function fontSizeFromWheelDelta(currentFontSize: number, deltaY: number): number {
  return clampEditorFontSize(currentFontSize * Math.exp(-deltaY * WHEEL_FONT_ZOOM_SENSITIVITY));
}

export function fontSizeFromGestureScale(startFontSize: number, scale: number): number {
  return clampEditorFontSize(startFontSize * scale);
}

export function createEditorWheelZoomController(idleMs = WHEEL_GESTURE_IDLE_MS) {
  const guard = createEditorWheelZoomGestureGuard(idleMs);
  let exactFontSize: number | null = null;
  let appliedGestureId = -1;

  return {
    nextFontSize(currentFontSize: number, event: EditorWheelZoomEvent): number | null {
      if (!guard.accepts(event)) {
        exactFontSize = null;
        appliedGestureId = guard.currentGestureId();
        return null;
      }
      const gestureId = guard.currentGestureId();
      let exact = exactFontSize;
      if (appliedGestureId !== gestureId || exact === null) {
        exact = currentFontSize;
        appliedGestureId = gestureId;
      }
      const sensitivity = isSyntheticPinchWheel(event) ? PINCH_WHEEL_ZOOM_SENSITIVITY : WHEEL_FONT_ZOOM_SENSITIVITY;
      exact = clampEditorFontSizeExact(exact * Math.exp(-event.deltaY * sensitivity));
      exactFontSize = exact;
      return clampEditorFontSize(exact);
    },
    rebase() {
      exactFontSize = null;
    },
    /** Keep trailing pinch inertia from starting a second zoom after a native gesture ends. */
    holdScrollGesture(timeStamp: number) {
      guard.holdAsPlainScroll(timeStamp);
      exactFontSize = null;
    },
    reset() {
      guard.reset();
      exactFontSize = null;
      appliedGestureId = -1;
    },
  };
}

export function createEditorZoomCommitScheduler(commit: (fontSize: number) => void, delayMs = 160) {
  let timer: ReturnType<typeof setTimeout> | null = null;
  let pendingFontSize: number | null = null;

  const clearPending = () => {
    if (timer) clearTimeout(timer);
    timer = null;
  };

  return {
    schedule(fontSize: number) {
      pendingFontSize = clampEditorFontSize(fontSize);
      clearPending();
      timer = setTimeout(() => {
        timer = null;
        const next = pendingFontSize;
        pendingFontSize = null;
        if (next !== null) commit(next);
      }, delayMs);
    },
    flush(fontSize?: number) {
      const next = clampEditorFontSize(fontSize ?? pendingFontSize ?? NaN);
      clearPending();
      pendingFontSize = null;
      if (!Number.isNaN(next)) commit(next);
    },
    hasPendingCommit() {
      return timer !== null;
    },
    dispose() {
      clearPending();
      pendingFontSize = null;
    },
  };
}
