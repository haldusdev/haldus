import { useTranslation } from "react-i18next"
import {
  BanIcon,
  CircleDashedIcon,
  PackageCheckIcon,
  TruckIcon,
} from "lucide-react"

import type { OrderStatus } from "@/api/model"
import { Badge } from "@/components/ui/badge"

const variants = {
  Draft: "outline",
  Placed: "secondary",
  Shipped: "default",
  Cancelled: "destructive",
} as const

const icons = {
  Draft: CircleDashedIcon,
  Placed: PackageCheckIcon,
  Shipped: TruckIcon,
  Cancelled: BanIcon,
} as const

/** Durum yalnızca renkle değil, simge ve metinle de ayırt edilir. */
export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  const { t } = useTranslation()
  const Icon = icons[status]

  return (
    <Badge variant={variants[status]}>
      <Icon data-icon="inline-start" aria-hidden="true" />
      {t(`orders.status.${status}`)}
    </Badge>
  )
}
