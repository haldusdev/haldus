import { useTranslation } from "react-i18next"
import { Navigate, Outlet, useLocation } from "react-router"

import { AppSidebar } from "@/components/app-sidebar"
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Separator } from "@/components/ui/separator"
import {
  SidebarInset,
  SidebarProvider,
  SidebarTrigger,
} from "@/components/ui/sidebar"
import { useSession } from "@/lib/session"

const sections = {
  products: "nav.products",
  categories: "nav.categories",
  orders: "nav.orders",
} as const

function isSection(value: string | undefined): value is keyof typeof sections {
  return value !== undefined && value in sections
}

/** Oturumu olan kullanıcının çerçevesi (shadcn sidebar-07): yan menü, üst çubuk ve sayfanın kendisi. */
export function DashboardLayout() {
  const { t } = useTranslation()
  const session = useSession()
  const location = useLocation()

  if (!session) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />
  }

  const section = location.pathname.split("/")[1]

  return (
    <SidebarProvider>
      <AppSidebar />
      <SidebarInset>
        <header className="flex h-16 shrink-0 items-center gap-2 transition-[width,height] ease-linear group-has-data-[collapsible=icon]/sidebar-wrapper:h-12">
          <div className="flex items-center gap-2 px-4">
            <SidebarTrigger className="-ml-1" />
            <Separator
              orientation="vertical"
              className="mr-2 data-[orientation=vertical]:h-4"
            />
            <Breadcrumb>
              <BreadcrumbList>
                <BreadcrumbItem className="hidden md:block">
                  {t("app.name")}
                </BreadcrumbItem>
                {isSection(section) ? (
                  <>
                    <BreadcrumbSeparator className="hidden md:block" />
                    <BreadcrumbItem>
                      <BreadcrumbPage>{t(sections[section])}</BreadcrumbPage>
                    </BreadcrumbItem>
                  </>
                ) : null}
              </BreadcrumbList>
            </Breadcrumb>
          </div>
        </header>
        <main className="flex flex-1 flex-col gap-4 p-4 pt-0">
          <Outlet />
        </main>
      </SidebarInset>
    </SidebarProvider>
  )
}
