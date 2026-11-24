import { HeartIcon } from 'lucide-react-native';
import { useTranslation } from 'react-i18next';
import { FlatList, RefreshControl, View } from 'react-native';

import { ProductRow } from '@/components/product-row';
import { EmptyState, ErrorBanner, ListSkeleton, LoadError } from '@/components/screen-state';
import { useBasket } from '@/lib/basket';
import { useFavorites } from '@/lib/favorites';

export default function FavoritesScreen() {
  const { t } = useTranslation();
  const favorites = useFavorites();
  const basket = useBasket();
  const busy = favorites.isSaving || basket.isSaving;

  return (
    <View className="flex-1 bg-background">
      <ErrorBanner error={favorites.writeError ?? basket.error} />
      {favorites.isPending ? (
        <ListSkeleton />
      ) : favorites.isError ? (
        <LoadError error={favorites.error} onRetry={() => void favorites.refetch()} />
      ) : (
        <FlatList
          data={favorites.products}
          keyExtractor={(product) => product.id}
          contentContainerClassName="flex-grow"
          renderItem={({ item }) => (
            <ProductRow
              product={item}
              favorite
              busy={busy}
              onToggleFavorite={() => void favorites.toggle(item.id).catch(() => undefined)}
              onAddToBasket={() => void basket.add(item.name).catch(() => undefined)}
            />
          )}
          ListEmptyComponent={
            <EmptyState
              icon={HeartIcon}
              title={t('favorites.empty')}
              description={t('favorites.emptyDescription')}
            />
          }
          refreshControl={
            <RefreshControl
              refreshing={favorites.isRefetching}
              onRefresh={() => void favorites.refetch()}
            />
          }
        />
      )}
    </View>
  );
}
