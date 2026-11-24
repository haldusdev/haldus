import type { LucideIcon } from 'lucide-react-native';
import { TriangleAlertIcon } from 'lucide-react-native';
import { useTranslation } from 'react-i18next';
import { View } from 'react-native';

import { Button } from '@/components/ui/button';
import { Icon } from '@/components/ui/icon';
import { Skeleton } from '@/components/ui/skeleton';
import { Text } from '@/components/ui/text';
import { problemMessage } from '@/lib/problem';

/** Liste yüklenirken satırların yerini tutar; yükseklikleri gerçek satırlarla aynıdır ki ekran zıplamasın. */
export function ListSkeleton({ rows = 6 }: { rows?: number }) {
  return (
    <View className="gap-3 p-4" accessibilityState={{ busy: true }}>
      {Array.from({ length: rows }, (_, row) => (
        <Skeleton key={row} className="h-16 w-full rounded-lg" />
      ))}
    </View>
  );
}

export function EmptyState({
  icon,
  title,
  description,
}: {
  icon: LucideIcon;
  title: string;
  description?: string;
}) {
  return (
    <View className="flex-1 items-center justify-center gap-2 p-8">
      <View className="mb-2 size-12 items-center justify-center rounded-full bg-muted">
        <Icon as={icon} size={22} className="text-muted-foreground" />
      </View>
      <Text className="text-center text-base font-semibold">{title}</Text>
      {description ? (
        <Text className="text-center text-sm text-muted-foreground">{description}</Text>
      ) : null}
    </View>
  );
}

export function LoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { t } = useTranslation();

  return (
    <View className="flex-1 items-center justify-center gap-2 p-8" role="alert">
      <View className="mb-2 size-12 items-center justify-center rounded-full bg-muted">
        <Icon as={TriangleAlertIcon} size={22} className="text-destructive" />
      </View>
      <Text className="text-center text-base font-semibold">{t('common.loadFailed')}</Text>
      <Text className="text-center text-sm text-muted-foreground">
        {problemMessage(error, t('common.unexpectedError'))}
      </Text>
      <Button variant="outline" className="mt-2 h-11" onPress={onRetry}>
        <Text>{t('common.retry')}</Text>
      </Button>
    </View>
  );
}

/** Bir yazma başarısız olduğunda listenin üstünde görünen kısa uyarı. */
export function ErrorBanner({ error }: { error: unknown }) {
  const { t } = useTranslation();

  if (!error) {
    return null;
  }

  return (
    <View
      className="mx-4 mt-3 rounded-md border border-destructive/40 bg-destructive/10 p-3"
      role="alert">
      <Text className="text-sm text-destructive">
        {problemMessage(error, t('common.unexpectedError'))}
      </Text>
    </View>
  );
}
