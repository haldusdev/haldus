import { useMutation, useQuery } from '@tanstack/react-query';

import type { ExternalProviderConfig } from '@/api/model';
import { signInWithProvider } from '@/lib/auth';
import { problemDetail } from '@/lib/problem';
import { prepareProviderSignIn } from '@/lib/provider';
import type { ProviderPrompt } from '@/lib/provider';

/**
 * Giriş ekranının kimlik sağlayıcı düğmesinin durumu. İstek ekran açılırken hazırlanır (keşif + PKCE), düğmeye
 * basılınca sağlayıcının sayfası açılır. Backend sağlayıcı sunmuyorsa (`null`) hiçbir şey yapmaz.
 */
export function useProviderSignIn(provider: ExternalProviderConfig | null) {
  const preparation = useQuery({
    queryKey: ['provider-sign-in', provider?.authority, provider?.clientId, provider?.scopes],
    queryFn: () => {
      if (!provider) {
        throw new Error('No identity provider is offered.');
      }

      return prepareProviderSignIn(provider);
    },
    enabled: provider !== null,
    // Hazırlanan istek bir girişe aittir: kendiliğinden yenilenmez ve ekran kapanınca önbellekte kalmaz.
    staleTime: Infinity,
    gcTime: 0,
  });

  const attempt = useMutation({
    mutationFn: async (prompt: ProviderPrompt) => {
      if (!provider) {
        throw new Error('No identity provider is offered.');
      }

      return signInWithProvider(provider, prompt);
    },
    onSettled: (outcome) => {
      // Durum ve PKCE değerleri tek bir girişe aittir; giriş tamamlanmadıysa sıradaki deneme için yenisi hazırlanır.
      if (outcome !== 'signedIn') {
        void preparation.refetch();
      }
    },
  });

  const { data: prompt, isFetching: preparing } = preparation;

  return {
    /** Giriş sürüyor: sağlayıcının sayfası açık ya da dönen kod işleniyor. */
    pending: attempt.isPending,
    /** Düğmeye basılamaz: giriş sürüyor ya da istek hazırlanıyor. */
    busy: attempt.isPending || preparing,
    cancelled: attempt.data === 'cancelled',
    /** Giriş başarısız oldu; backend bir gerekçe yazdıysa (ör. hesap kabul edilmedi) `detail` odur. */
    failure: attempt.isError ? { detail: problemDetail(attempt.error) } : undefined,
    /** Sağlayıcının uçları okunamadı; düğme yeniden dener. */
    unavailable: preparation.isError && !preparing,
    start() {
      if (attempt.isPending || preparing) {
        return;
      }

      if (prompt && !preparation.isError) {
        attempt.mutate(prompt);
      } else {
        void preparation.refetch();
      }
    },
    reset: attempt.reset,
  };
}
