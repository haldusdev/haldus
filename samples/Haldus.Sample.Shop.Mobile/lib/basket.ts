import { useQueryClient } from '@tanstack/react-query';

import {
  getListBasketsQueryKey,
  listBaskets,
  useCreateBasket,
  useListBaskets,
  useUpdateBasket,
} from '@/api/baskets/baskets';
import type { UpdateBasketItemRequest } from '@/api/model';

const MAX_QUANTITY = 1000;
const PARAMS = { pageSize: 1 } as const;

// Değişiklikler sıraya girer ve her biri sepetin o andaki halini okur. Sepet bir bütün olarak yazıldığı için, bir
// önceki değişikliğin sonucu görülmeden başlayan ikinci değişiklik onu ezerdi; henüz sepeti olmayan kullanıcının art
// arda iki dokunuşu ise iki ayrı sepet oluştururdu. Sıra, hook'u kullanan bütün ekranlar için ortaktır.
let queue: Promise<unknown> = Promise.resolve();

/**
 * Kullanıcının sepeti (`/me/baskets`). Sepet, satırlarıyla birlikte bir bütündür: satırlar sepetin isteğinde gider
 * ve backend onları eşitler (kimliği verilen güncellenir, verilmeyen eklenir, istekte olmayan silinir). Bu yüzden
 * her değişiklik satırların tamamını gönderir. İlk ürün eklendiğinde sepet oluşturulur.
 *
 * Yalnızca orval'ın ürettiği hook'ları birleştirir; kendi isteğini veya tipini yazmaz.
 */
export function useBasket() {
  const queryClient = useQueryClient();
  const baskets = useListBaskets(PARAMS);
  const create = useCreateBasket();
  const update = useUpdateBasket();

  const basket = baskets.data?.data.items[0] ?? null;

  type Edit = (lines: UpdateBasketItemRequest[]) => UpdateBasketItemRequest[];

  async function apply(edit: Edit) {
    // Ekranın çizildiği andaki değil, sıradaki bir önceki değişiklikten sonraki sepet okunur.
    const loaded = await queryClient.ensureQueryData({
      queryKey: getListBasketsQueryKey(PARAMS),
      queryFn: () => listBaskets(PARAMS),
    });
    const latest = loaded.data.items[0] ?? null;

    const current: UpdateBasketItemRequest[] = (latest?.basketItems ?? []).map((item) => ({
      id: item.id,
      productName: item.productName,
      quantity: item.quantity,
    }));

    const lines = edit(current)
      .filter((line) => (line.quantity ?? 0) > 0)
      .map((line) => ({ ...line, quantity: Math.min(line.quantity ?? 1, MAX_QUANTITY) }));

    try {
      if (latest) {
        await update.mutateAsync({
          id: latest.id,
          data: { note: latest.note, basketItems: lines },
        });
      } else {
        await create.mutateAsync({ data: { basketItems: lines } });
      }
    } finally {
      // Sıradaki değişiklik güncel sepeti görsün diye yeniden okuma beklenir; hata durumunda da (istek sunucuya
      // ulaşmış olabilir).
      await queryClient.refetchQueries({ queryKey: getListBasketsQueryKey() });
    }
  }

  function change(edit: Edit): Promise<void> {
    const next = queue.then(() => apply(edit));
    queue = next.catch(() => undefined);
    return next;
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
