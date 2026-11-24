import { useQueryClient } from '@tanstack/react-query';

import {
  getListBasketsQueryKey,
  useCreateBasket,
  useListBaskets,
  useUpdateBasket,
} from '@/api/baskets/baskets';
import type { UpdateBasketItemRequest } from '@/api/model';

const MAX_QUANTITY = 1000;

/**
 * Kullanıcının sepeti (`/me/baskets`). Sepet, satırlarıyla birlikte bir bütündür: satırlar sepetin isteğinde gider
 * ve backend onları eşitler (kimliği verilen güncellenir, verilmeyen eklenir, istekte olmayan silinir). Bu yüzden
 * her değişiklik satırların tamamını gönderir. İlk ürün eklendiğinde sepet oluşturulur.
 *
 * Yalnızca orval'ın ürettiği hook'ları birleştirir; kendi isteğini veya tipini yazmaz.
 */
export function useBasket() {
  const queryClient = useQueryClient();
  const baskets = useListBaskets({ pageSize: 1 });
  const create = useCreateBasket();
  const update = useUpdateBasket();

  const basket = baskets.data?.data.items[0] ?? null;

  async function change(edit: (lines: UpdateBasketItemRequest[]) => UpdateBasketItemRequest[]) {
    const current: UpdateBasketItemRequest[] = (basket?.basketItems ?? []).map((item) => ({
      id: item.id,
      productName: item.productName,
      quantity: item.quantity,
    }));

    const lines = edit(current)
      .filter((line) => (line.quantity ?? 0) > 0)
      .map((line) => ({ ...line, quantity: Math.min(line.quantity ?? 1, MAX_QUANTITY) }));

    if (basket) {
      await update.mutateAsync({ id: basket.id, data: { note: basket.note, basketItems: lines } });
    } else {
      await create.mutateAsync({ data: { basketItems: lines } });
    }

    await queryClient.invalidateQueries({ queryKey: getListBasketsQueryKey() });
  }

  return {
    basket,
    isPending: baskets.isPending,
    isError: baskets.isError,
    error: baskets.error ?? create.error ?? update.error,
    isSaving: create.isPending || update.isPending,
    refetch: baskets.refetch,
    isRefetching: baskets.isRefetching,

    /** Ürünü sepete ekler; zaten varsa adedini artırır. */
    add: (productName: string) =>
      change((lines) =>
        lines.some((line) => line.productName === productName)
          ? lines.map((line) =>
              line.productName === productName
                ? { ...line, quantity: (line.quantity ?? 0) + 1 }
                : line
            )
          : [...lines, { productName, quantity: 1 }]
      ),

    /** Satırın adedini değiştirir; sıfıra inen satır sepetten çıkar. */
    setQuantity: (id: string, quantity: number) =>
      change((lines) => lines.map((line) => (line.id === id ? { ...line, quantity } : line))),

    clear: () => change(() => []),
  };
}
