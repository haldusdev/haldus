import * as React from "react"

const MOBILE_BREAKPOINT = 768
const QUERY = `(max-width: ${MOBILE_BREAKPOINT - 1}px)`

function subscribe(onChange: () => void) {
  const mql = window.matchMedia(QUERY)
  mql.addEventListener("change", onChange)
  return () => mql.removeEventListener("change", onChange)
}

// Ekran genişliği React'in dışındaki bir durumdur; effect içinde state yazmak yerine doğrudan ondan okunur.
export function useIsMobile() {
  return React.useSyncExternalStore(
    subscribe,
    () => window.matchMedia(QUERY).matches,
    // Tarayıcı dışında (sunucuda çizilen testlerde) ekran yoktur; geniş ekran varsayılır.
    () => false
  )
}
