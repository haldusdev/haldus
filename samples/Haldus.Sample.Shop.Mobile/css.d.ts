// Expo bu bildirimi `expo start` sırasında ürettiği expo-env.d.ts ile de sağlar; o dosya commit'lenmez. Tip denetimi
// uygulama hiç başlatılmadan da (ör. CI'da) geçsin diye burada da yazılıdır.
declare module '*.css';
