import { useQueryClient } from '@tanstack/react-query';

import {
  getListFavoritesQueryKey,
  useDeleteFavorite,
  useListFavorites,
  usePutFavorite,
} from '@/api/favorites/favorites';

/**
 * Kullanıcının favorileri (`/me/favorites`). Favori, kullanıcıyı bir ürüne bağlayan bir işarettir: `PUT` kurar,
 * `DELETE` kaldırır, liste bağları değil ürünlerin kendisini döndürür.
 *
 * Yalnızca orval'ın ürettiği hook'ları birleştirir; kendi isteğini veya tipini yazmaz.
 */
export function useFavorites() {
  const queryClient = useQueryClient();
  const favorites = useListFavorites({ pageSize: 100 });
  const put = usePutFavorite();
  const remove = useDeleteFavorite();

  const products = favorites.data?.data.items ?? [];
  const ids = new Set(products.map((product) => product.id));

  async function toggle(productId: string) {
    if (ids.has(productId)) {
      await remove.mutateAsync({ productId });
    } else {
      await put.mutateAsync({ productId });
    }

    await queryClient.invalidateQueries({ queryKey: getListFavoritesQueryKey() });
  }

  return {
    products,
    has: (productId: string) => ids.has(productId),
    toggle,
    isPending: favorites.isPending,
    isError: favorites.isError,
    error: favorites.error,
    writeError: put.error ?? remove.error,
    isSaving: put.isPending || remove.isPending,
    refetch: favorites.refetch,
    isRefetching: favorites.isRefetching,
  };
}
