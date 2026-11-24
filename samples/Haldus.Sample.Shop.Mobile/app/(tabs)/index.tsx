import { PackageIcon } from 'lucide-react-native';
import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { ActivityIndicator, FlatList, RefreshControl, View } from 'react-native';

import { useListProductsInfinite } from '@/api/products/products';
import { ProductRow } from '@/components/product-row';
import { EmptyState, ErrorBanner, ListSkeleton, LoadError } from '@/components/screen-state';
import { Input } from '@/components/ui/input';
import { useBasket } from '@/lib/basket';
import { useFavorites } from '@/lib/favorites';

const PAGE_SIZE = 20;

function useDebounced<T>(value: T, delay: number): T {
  const [debounced, setDebounced] = React.useState(value);

  React.useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);

  return debounced;
}

export default function ProductsScreen() {
  const { t } = useTranslation();
  const [search, setSearch] = React.useState('');
  const term = useDebounced(search.trim(), 300);

  const products = useListProductsInfinite(
    { pageSize: PAGE_SIZE, sort: 'name', search: term.length > 0 ? term : undefined },
    {
      query: {
        initialPageParam: 1,
        // Backend toplam sayıyı döndürür; kalan kayıt varsa sıradaki sayfa istenir.
        getNextPageParam: (last) =>
          last.data.page * last.data.pageSize < last.data.totalCount
            ? last.data.page + 1
            : undefined,
      },
    }
  );
  const favorites = useFavorites();
  const basket = useBasket();

  const items = products.data?.pages.flatMap((page) => page.data.items) ?? [];
  const busy = favorites.isSaving || basket.isSaving;

  return (
    <View className="flex-1 bg-background">
      <View className="border-b border-border p-4">
        <Input
          value={search}
          onChangeText={setSearch}
          placeholder={t('products.searchPlaceholder')}
          accessibilityLabel={t('products.searchPlaceholder')}
          autoCapitalize="none"
          autoCorrect={false}
          returnKeyType="search"
          clearButtonMode="while-editing"
          className="h-11"
        />
      </View>
      <ErrorBanner error={favorites.writeError ?? basket.error} />
      {products.isPending ? (
        <ListSkeleton />
      ) : products.isError ? (
        <LoadError error={products.error} onRetry={() => void products.refetch()} />
      ) : (
        <FlatList
          data={items}
          keyExtractor={(product) => product.id}
          contentContainerClassName="flex-grow"
          keyboardShouldPersistTaps="handled"
          renderItem={({ item }) => (
            <ProductRow
              product={item}
              favorite={favorites.has(item.id)}
              busy={busy}
              onToggleFavorite={() => void favorites.toggle(item.id).catch(() => undefined)}
              onAddToBasket={() => void basket.add(item.name).catch(() => undefined)}
            />
          )}
          ListEmptyComponent={
            <EmptyState
              icon={PackageIcon}
              title={term.length > 0 ? t('products.emptySearch') : t('products.empty')}
              description={term.length > 0 ? undefined : t('products.emptyDescription')}
            />
          }
          ListFooterComponent={
            products.isFetchingNextPage ? <ActivityIndicator className="py-4" /> : null
          }
          onEndReachedThreshold={0.5}
          onEndReached={() => {
            if (products.hasNextPage && !products.isFetchingNextPage) {
              void products.fetchNextPage();
            }
          }}
          refreshControl={
            <RefreshControl
              refreshing={products.isRefetching && !products.isFetchingNextPage}
              onRefresh={() => void products.refetch()}
            />
          }
        />
      )}
    </View>
  );
}
