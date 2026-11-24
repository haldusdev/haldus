import { MinusIcon, PlusIcon, ShoppingBasketIcon, Trash2Icon } from 'lucide-react-native';
import { useTranslation } from 'react-i18next';
import { FlatList, RefreshControl, View } from 'react-native';

import { EmptyState, ErrorBanner, ListSkeleton, LoadError } from '@/components/screen-state';
import { Button } from '@/components/ui/button';
import { Icon } from '@/components/ui/icon';
import { Text } from '@/components/ui/text';
import { useBasket } from '@/lib/basket';
import { formatDate } from '@/lib/format';

export default function BasketScreen() {
  const { t } = useTranslation();
  const basket = useBasket();
  const lines = basket.basket?.basketItems ?? [];

  if (basket.isPending) {
    return <ListSkeleton rows={3} />;
  }

  if (basket.isError) {
    return <LoadError error={basket.error} onRetry={() => void basket.refetch()} />;
  }

  return (
    <View className="flex-1 bg-background">
      <ErrorBanner error={basket.error} />
      <FlatList
        data={lines}
        keyExtractor={(line) => line.id}
        contentContainerClassName="flex-grow"
        renderItem={({ item }) => (
          <View className="flex-row items-center gap-2 border-b border-border px-4 py-3">
            <View className="min-w-0 flex-1 gap-0.5">
              <Text className="font-medium" numberOfLines={1}>
                {item.productName}
              </Text>
              <Text className="text-sm text-muted-foreground">
                {t('basket.quantity', { count: item.quantity })}
              </Text>
            </View>
            <Button
              variant="outline"
              size="icon"
              className="size-11"
              disabled={basket.isSaving}
              accessibilityLabel={
                item.quantity === 1
                  ? t('basket.remove', { name: item.productName })
                  : t('basket.decrease', { name: item.productName })
              }
              onPress={() =>
                void basket.setQuantity(item.id, item.quantity - 1).catch(() => undefined)
              }>
              <Icon as={item.quantity === 1 ? Trash2Icon : MinusIcon} size={18} />
            </Button>
            <Text
              className="w-8 text-center font-medium tabular-nums"
              accessibilityLabel={t('basket.quantity', { count: item.quantity })}>
              {item.quantity}
            </Text>
            <Button
              variant="outline"
              size="icon"
              className="size-11"
              disabled={basket.isSaving}
              accessibilityLabel={t('basket.increase', { name: item.productName })}
              onPress={() =>
                void basket.setQuantity(item.id, item.quantity + 1).catch(() => undefined)
              }>
              <Icon as={PlusIcon} size={18} />
            </Button>
          </View>
        )}
        ListEmptyComponent={
          <EmptyState
            icon={ShoppingBasketIcon}
            title={t('basket.empty')}
            description={t('basket.emptyDescription')}
          />
        }
        ListFooterComponent={
          lines.length > 0 ? (
            <View className="gap-3 p-4">
              <Text className="text-sm text-muted-foreground">
                {t('basket.lines', { count: lines.length })}
                {/* Sepet kullanıldıkça yaşar: her yazma ömrünü yeniden başlatır (Expiring, sliding). */}
                {basket.basket?.expiresAt
                  ? ` · ${t('basket.expires', { date: formatDate(basket.basket.expiresAt) })}`
                  : ''}
              </Text>
              <Button
                variant="outline"
                className="h-11"
                disabled={basket.isSaving}
                onPress={() => void basket.clear().catch(() => undefined)}>
                <Text>{t('basket.clear')}</Text>
              </Button>
            </View>
          ) : null
        }
        refreshControl={
          <RefreshControl
            refreshing={basket.isRefetching}
            onRefresh={() => void basket.refetch()}
          />
        }
      />
    </View>
  );
}
