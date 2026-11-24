import { Link, Stack } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { View } from 'react-native';

import { Button } from '@/components/ui/button';
import { Text } from '@/components/ui/text';

export default function NotFoundScreen() {
  const { t } = useTranslation();

  return (
    <>
      <Stack.Screen options={{ title: t('app.name') }} />
      <View className="flex-1 items-center justify-center gap-4 bg-background p-8">
        <Text className="text-center text-base font-semibold">{t('common.notFound')}</Text>
        <Link href="/" asChild>
          <Button variant="outline" className="h-11">
            <Text>{t('common.goHome')}</Text>
          </Button>
        </Link>
      </View>
    </>
  );
}
