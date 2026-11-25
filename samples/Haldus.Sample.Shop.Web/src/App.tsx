import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { useTranslation } from "react-i18next"
import { BrowserRouter, Link, Navigate, Route, Routes } from "react-router"
import { CompassIcon } from "lucide-react"

import { DashboardLayout } from "@/components/dashboard-layout"
import { EmptyState } from "@/components/query-state"
import { Button } from "@/components/ui/button"
import { Toaster } from "@/components/ui/sonner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { problemStatus } from "@/lib/problem"
import { CALLBACK_PATH } from "@/lib/provider"
import { getSession, subscribeSession } from "@/lib/session"
import { CategoriesPage } from "@/pages/categories"
import { LoginPage } from "@/pages/login"
import { LoginCallbackPage } from "@/pages/login-callback"
import { OrderDetailPage } from "@/pages/order-detail"
import { OrdersPage } from "@/pages/orders"
import { ProductsPage } from "@/pages/products"

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      // İstemcinin hatası (4xx) yinelenince düzelmez; yalnızca ağ ve sunucu hataları bir kez daha denenir.
      retry: (failureCount, error) => {
        const status = problemStatus(error)
        return failureCount < 1 && (status === undefined || status >= 500)
      },
    },
  },
})

// Önbellekteki veri onu okuyan hesaba aittir; hesap değişince (çıkış, başka hesapla giriş) boşaltılır.
// Token'ın yenilenmesi hesabı değiştirmez.
let cachedFor = getSession()?.accountId

subscribeSession(() => {
  const accountId = getSession()?.accountId

  if (accountId !== cachedFor) {
    cachedFor = accountId
    queryClient.clear()
  }
})

function NotFoundPage() {
  const { t } = useTranslation()

  return (
    <EmptyState
      icon={<CompassIcon aria-hidden="true" />}
      title={t("common.notFound")}
      description={t("common.notFoundDescription")}
    >
      <Button asChild variant="outline">
        <Link to="/products">{t("common.backToProducts")}</Link>
      </Button>
    </EmptyState>
  )
}

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <BrowserRouter>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path={CALLBACK_PATH} element={<LoginCallbackPage />} />
            <Route element={<DashboardLayout />}>
              <Route index element={<Navigate to="/products" replace />} />
              <Route path="products" element={<ProductsPage />} />
              <Route path="categories" element={<CategoriesPage />} />
              <Route path="orders" element={<OrdersPage />} />
              <Route path="orders/:id" element={<OrderDetailPage />} />
              <Route path="*" element={<NotFoundPage />} />
            </Route>
          </Routes>
        </BrowserRouter>
        <Toaster richColors closeButton />
      </TooltipProvider>
    </QueryClientProvider>
  )
}

export default App
