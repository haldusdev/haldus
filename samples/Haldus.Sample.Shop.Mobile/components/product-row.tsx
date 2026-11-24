import { HeartIcon, PlusIcon } from 'lucide-react-native';
import { useTranslation } from 'react-i18next';
import { View } from 'react-native';

import type { ProductResponse } from '@/api/model';
import { Button } from '@/components/ui/button';
import { Icon } from '@/components/ui/icon';
import { Text } from '@/components/ui/text';
import { formatAmount } from '@/lib/format';

/** Ürün ve favori listelerinin satırı: ad, fiyat, favori düğmesi ve sepete ekleme. */
export function ProductRow({
  product,
  favorite,
  busy,
  onToggleFavorite,
  onAddToBasket,
}: {
  product: ProductResponse;
  favorite: boolean;
  busy: boolean;
  onToggleFavorite: () => void;
  onAddToBasket: () => void;
}) {
  const { t } = useTranslation();

  return (
    <View className="flex-row items-center gap-3 border-b border-border px-4 py-3">
      <View className="min-w-0 flex-1 gap-0.5">
        <Text className="font-medium" numberOfLines={1}>
          {product.name}
        </Text>
        {product.description ? (
          <Text className="text-sm text-muted-foreground" numberOfLines={1}>
            {product.description}
          </Text>
        ) : null}
        <Text className="text-sm tabular-nums text-muted-foreground">
          {formatAmount(product.price)}
        </Text>
      </View>
      <Button
        variant="ghost"
        size="icon"
        className="size-11"
        disabled={busy}
        accessibilityLabel={t(favorite ? 'products.removeFavorite' : 'products.addFavorite', {
          name: product.name,
        })}
        accessibilityState={{ selected: favorite }}
        onPress={onToggleFavorite}>
        {/* Favori yalnızca renkle değil, dolu kalple de ayırt edilir. */}
        <Icon
          as={HeartIcon}
          size={20}
          className={favorite ? 'text-destructive' : 'text-muted-foreground'}
          fill={favorite ? 'currentColor' : 'none'}
        />
      </Button>
      <Button
        variant="outline"
        size="icon"
        className="size-11"
        disabled={busy}
        accessibilityLabel={t('products.addToBasketLabel', { name: product.name })}
        onPress={onAddToBasket}>
        <Icon as={PlusIcon} size={20} />
      </Button>
    </View>
  );
}
