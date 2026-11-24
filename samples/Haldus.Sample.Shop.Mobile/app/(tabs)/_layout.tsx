import { Tabs } from 'expo-router';
import { HeartIcon, PackageIcon, ShoppingBasketIcon, UserIcon } from 'lucide-react-native';
import { useTranslation } from 'react-i18next';

import { useBasket } from '@/lib/basket';

export default function TabsLayout() {
  const { t } = useTranslation();
  const { basket } = useBasket();
  const lines = basket?.basketItems.length ?? 0;

  return (
    <Tabs>
      <Tabs.Screen
        name="index"
        options={{
          title: t('tabs.products'),
          tabBarIcon: ({ color, size }) => <PackageIcon color={color} size={size} />,
        }}
      />
      <Tabs.Screen
        name="favorites"
        options={{
          title: t('tabs.favorites'),
          tabBarIcon: ({ color, size }) => <HeartIcon color={color} size={size} />,
        }}
      />
      <Tabs.Screen
        name="basket"
        options={{
          title: t('tabs.basket'),
          tabBarBadge: lines > 0 ? lines : undefined,
          tabBarIcon: ({ color, size }) => <ShoppingBasketIcon color={color} size={size} />,
        }}
      />
      <Tabs.Screen
        name="account"
        options={{
          title: t('tabs.account'),
          tabBarIcon: ({ color, size }) => <UserIcon color={color} size={size} />,
        }}
      />
    </Tabs>
  );
}
