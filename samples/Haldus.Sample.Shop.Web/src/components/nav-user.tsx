import { useTranslation } from "react-i18next"
import { ChevronsUpDownIcon, LogOutIcon } from "lucide-react"

import { useTheme } from "@/components/theme-provider"
import { Avatar, AvatarFallback } from "@/components/ui/avatar"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  useSidebar,
} from "@/components/ui/sidebar"
import { languages } from "@/i18n"
import { signOut, useSession } from "@/lib/session"

const themes = ["light", "dark", "system"] as const

const themeLabels = {
  light: "nav.themeLight",
  dark: "nav.themeDark",
  system: "nav.themeSystem",
} as const

export function NavUser() {
  const { isMobile } = useSidebar()
  const { t, i18n } = useTranslation()
  const { theme, setTheme } = useTheme()
  const session = useSession()

  if (!session) {
    return null
  }

  const tenant = t("nav.tenant", { tenant: session.tenantId.slice(0, 8) })
  const initials = session.userId.slice(0, 2).toUpperCase()

  const identity = (
    <>
      <Avatar className="h-8 w-8 rounded-lg">
        <AvatarFallback className="rounded-lg">{initials}</AvatarFallback>
      </Avatar>
      <div className="grid flex-1 text-left text-sm leading-tight">
        <span className="truncate font-medium">{session.userId}</span>
        <span className="truncate text-xs">{tenant}</span>
      </div>
    </>
  )

  return (
    <SidebarMenu>
      <SidebarMenuItem>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <SidebarMenuButton
              size="lg"
              aria-label={t("nav.userMenu")}
              className="data-[state=open]:bg-sidebar-accent data-[state=open]:text-sidebar-accent-foreground"
            >
              {identity}
              <ChevronsUpDownIcon
                className="ml-auto size-4"
                aria-hidden="true"
              />
            </SidebarMenuButton>
          </DropdownMenuTrigger>
          <DropdownMenuContent
            className="min-w-56"
            side={isMobile ? "bottom" : "right"}
            align="end"
            sideOffset={4}
          >
            <DropdownMenuLabel className="p-0 font-normal">
              <div className="flex items-center gap-2 px-1 py-1.5 text-left text-sm">
                {identity}
              </div>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-xs text-muted-foreground">
              {t("nav.language")}
            </DropdownMenuLabel>
            <DropdownMenuRadioGroup
              value={i18n.language}
              onValueChange={(language) => void i18n.changeLanguage(language)}
            >
              {languages.map((language) => (
                <DropdownMenuRadioItem key={language} value={language}>
                  {t(`languages.${language}`)}
                </DropdownMenuRadioItem>
              ))}
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-xs text-muted-foreground">
              {t("nav.theme")}
            </DropdownMenuLabel>
            <DropdownMenuRadioGroup
              value={theme}
              onValueChange={(next) => {
                const selected = themes.find((candidate) => candidate === next)

                if (selected) {
                  setTheme(selected)
                }
              }}
            >
              {themes.map((option) => (
                <DropdownMenuRadioItem key={option} value={option}>
                  {t(themeLabels[option])}
                </DropdownMenuRadioItem>
              ))}
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={signOut}>
              <LogOutIcon aria-hidden="true" />
              {t("nav.signOut")}
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </SidebarMenuItem>
    </SidebarMenu>
  )
}
