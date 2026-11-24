import * as React from "react"
import { useTranslation } from "react-i18next"
import {
  FolderTreeIcon,
  PackageIcon,
  ReceiptTextIcon,
  StoreIcon,
} from "lucide-react"

import { NavMain, type NavGroup } from "@/components/nav-main"
import { NavUser } from "@/components/nav-user"
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
} from "@/components/ui/sidebar"

export function AppSidebar({ ...props }: React.ComponentProps<typeof Sidebar>) {
  const { t } = useTranslation()

  const groups: NavGroup[] = [
    {
      label: t("nav.catalog"),
      items: [
        { title: t("nav.products"), to: "/products", icon: <PackageIcon /> },
        {
          title: t("nav.categories"),
          to: "/categories",
          icon: <FolderTreeIcon />,
        },
      ],
    },
    {
      label: t("nav.sales"),
      items: [
        { title: t("nav.orders"), to: "/orders", icon: <ReceiptTextIcon /> },
      ],
    },
  ]

  return (
    <Sidebar collapsible="icon" {...props}>
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" className="pointer-events-none">
              <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-sidebar-primary text-sidebar-primary-foreground">
                <StoreIcon aria-hidden="true" />
              </div>
              <div className="grid flex-1 text-left text-sm leading-tight">
                <span className="truncate font-medium">{t("app.name")}</span>
                <span className="truncate text-xs">{t("app.tagline")}</span>
              </div>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>
      <SidebarContent>
        <NavMain groups={groups} />
      </SidebarContent>
      <SidebarFooter>
        <NavUser />
      </SidebarFooter>
      <SidebarRail />
    </Sidebar>
  )
}
