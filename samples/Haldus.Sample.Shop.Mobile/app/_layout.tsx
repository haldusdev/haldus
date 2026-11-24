import '@/global.css';

import { PortalHost } from '@rn-primitives/portal';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Stack } from 'expo-router';
import { ThemeProvider } from 'expo-router/react-navigation';
import * as SplashScreen from 'expo-splash-screen';
import { StatusBar } from 'expo-status-bar';
import { useColorScheme } from 'nativewind';
import * as React from 'react';
import { useTranslation } from 'react-i18next';

import { restoreLanguage } from '@/i18n';
import '@/lib/auth';
import { problemStatus } from '@/lib/problem';
import { getSession, restoreSession, subscribeSession, useSession } from '@/lib/session';
import { NAV_THEME } from '@/lib/theme';

export {
  // Yerleşim bileşeninde fırlatılan hataları yakalar.
  ErrorBoundary,
} from 'expo-router';

// Saklanan oturum ve dil okunana kadar açılış ekranı kalır; böylece giriş ekranı bir an görünüp kaybolmaz.
void SplashScreen.preventAutoHideAsync();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      // İstemcinin hatası (4xx) yinelenince düzelmez; yalnızca ağ ve sunucu hataları bir kez daha denenir.
      retry: (failureCount, error) => {
        const status = problemStatus(error);
        return failureCount < 1 && (status === undefined || status >= 500);
      },
    },
  },
});

// Önbellekteki veri onu okuyan hesaba aittir; hesap değişince (çıkış, süresi dolan oturum, başka hesapla giriş)
// boşaltılır. Token'ın yenilenmesi hesabı değiştirmez.
let cachedFor = getSession()?.accountId;

subscribeSession(() => {
  const accountId = getSession()?.accountId;

  if (accountId !== cachedFor) {
    cachedFor = accountId;
    queryClient.clear();
  }
});
export default function RootLayout() {
  const { colorScheme } = useColorScheme();
  const { t } = useTranslation();
  const session = useSession();
  const [ready, setReady] = React.useState(false);

  React.useEffect(() => {
    void Promise.all([restoreSession(), restoreLanguage()]).finally(() => {
      setReady(true);
      void SplashScreen.hideAsync();
    });
  }, []);

  if (!ready) {
    return null;
  }

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider value={NAV_THEME[colorScheme ?? 'light']}>
        <StatusBar style={colorScheme === 'dark' ? 'light' : 'dark'} />
        <Stack>
          {/* Oturum yoksa sekmeler, varsa giriş ekranı yığında hiç yer almaz; geri tuşu da oraya götürmez. */}
          <Stack.Protected guard={session !== null}>
            <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
          </Stack.Protected>
          <Stack.Protected guard={session === null}>
            <Stack.Screen name="sign-in" options={{ title: t('signIn.title') }} />
          </Stack.Protected>
        </Stack>
        <PortalHost />
      </ThemeProvider>
    </QueryClientProvider>
  );
}
