import { useQueryClient } from '@tanstack/react-query';
import { useColorScheme } from 'nativewind';
import { useTranslation } from 'react-i18next';
import { ScrollView, View } from 'react-native';

import { ChangePassword, VerifyEmail } from '@/components/account-security';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { Text } from '@/components/ui/text';
import { languages, setLanguage } from '@/i18n';
import { API_URL } from '@/lib/api-fetch';
import { closeSession } from '@/lib/auth';
import { useSession } from '@/lib/session';

const themes = ['light', 'dark'] as const;

const themeLabels = {
  light: 'account.themeLight',
  dark: 'account.themeDark',
} as const;

function Row({ label, value }: { label: string; value: string }) {
  return (
    <View className="gap-0.5 py-3">
      <Text className="text-sm text-muted-foreground">{label}</Text>
      <Text selectable>{value}</Text>
    </View>
  );
}

export default function AccountScreen() {
  const { t, i18n } = useTranslation();
  const { colorScheme, setColorScheme } = useColorScheme();
  const queryClient = useQueryClient();
  const session = useSession();

  return (
    <ScrollView
      className="flex-1 bg-background"
      contentContainerClassName="p-4"
      keyboardShouldPersistTaps="handled">
      <VerifyEmail />
      <Row label={t('account.user')} value={session?.email ?? ''} />
      <Separator />
      <Row label={t('account.tenant')} value={session?.tenantId ?? ''} />
      <Separator />
      <Row label={t('account.server')} value={API_URL} />
      <Separator />

      <View className="gap-2 py-3">
        <Text className="text-sm text-muted-foreground">{t('account.language')}</Text>
        <View className="flex-row gap-2" role="radiogroup" aria-label={t('account.language')}>
          {languages.map((language) => (
            <Button
              key={language}
              variant={i18n.language === language ? 'default' : 'outline'}
              className="h-11 flex-1"
              role="radio"
              // `aria-checked` cihazda da web'de de okunur; `accessibilityState` web'e hiç yansımıyordu.
              aria-checked={i18n.language === language}
              // Dil backend'e de gider; önbellekteki yanıtlar eski dilde olabileceği için yenilenir.
              onPress={() =>
                void setLanguage(language).then(() => queryClient.invalidateQueries())
              }>
              <Text>{t(`languages.${language}`)}</Text>
            </Button>
          ))}
        </View>
      </View>
      <Separator />

      <View className="gap-2 py-3">
        <Text className="text-sm text-muted-foreground">{t('account.theme')}</Text>
        <View className="flex-row gap-2" role="radiogroup" aria-label={t('account.theme')}>
          {themes.map((theme) => (
            <Button
              key={theme}
              variant={(colorScheme ?? 'light') === theme ? 'default' : 'outline'}
              className="h-11 flex-1"
              role="radio"
              aria-checked={(colorScheme ?? 'light') === theme}
              onPress={() => setColorScheme(theme)}>
              <Text>{t(themeLabels[theme])}</Text>
            </Button>
          ))}
        </View>
      </View>
      <Separator />

      <ChangePassword />
      <Separator />

      <Button
        variant="destructive"
        className="mt-6 h-11"
        onPress={() => {
          // Başka bir kullanıcının verisi önbellekte kalmasın.
          queryClient.clear();
          void closeSession();
        }}>
        <Text>{t('account.signOut')}</Text>
      </Button>
    </ScrollView>
  );
}
