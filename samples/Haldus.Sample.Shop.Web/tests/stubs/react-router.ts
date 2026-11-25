// Bileşen testlerinde kaynak kodun `react-router` içe aktarımı buraya gelir (support/ui.tsx). Her şey gerçeğidir;
// yalnızca `Navigate` değişir. Gerçeği hiçbir şey çizmez ve yönlendirmeyi bir effect içinde yapar; sunucuda effect
// çalışmadığı için nereye yönlendirdiği görülemez. Bu taklit de bir şey çizmez ama istenen yönlendirmeyi kaydeder.
import { useInRouterContext, type NavigateProps } from "react-router"

export * from "react-router"

export const redirects: NavigateProps[] = []

export function Navigate(props: NavigateProps): null {
  if (!useInRouterContext()) {
    throw new Error("<Navigate> was rendered outside a router.")
  }

  redirects.push(props)
  return null
}
