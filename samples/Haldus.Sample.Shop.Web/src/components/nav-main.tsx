import { NavLink, useLocation } from "react-router"

import {
  SidebarGroup,
  SidebarGroupLabel,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
} from "@/components/ui/sidebar"

export type NavGroup = {
  label: string
  items: {
    title: string
    to: string
    icon: React.ReactNode
  }[]
}

export function NavMain({ groups }: { groups: NavGroup[] }) {
  const { pathname } = useLocation()

  return (
    <>
      {groups.map((group) => (
        <SidebarGroup key={group.label}>
          <SidebarGroupLabel>{group.label}</SidebarGroupLabel>
          <SidebarMenu>
            {group.items.map((item) => (
              <SidebarMenuItem key={item.to}>
                <SidebarMenuButton
                  asChild
                  tooltip={item.title}
                  isActive={
                    pathname === item.to || pathname.startsWith(`${item.to}/`)
                  }
                >
                  <NavLink to={item.to}>
                    {item.icon}
                    <span>{item.title}</span>
                  </NavLink>
                </SidebarMenuButton>
              </SidebarMenuItem>
            ))}
          </SidebarMenu>
        </SidebarGroup>
      ))}
    </>
  )
}
