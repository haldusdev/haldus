// `oidc-client-ts` yerine yalnızca onu isteyen testlerde yüklenir (support/provider.ts). Gerçeği ağa ve tarayıcıya
// (yönlendirme, `crypto.subtle`) ihtiyaç duyar. Bu taklit protokolü çalıştırmaz; kitaplığın, panelin dayandığı
// davranışlarını taşır: kullanıcı depodan okunur, yenileme token'ı yoksa `signinSilent` iframe'e düşer (burada hata
// verir), yenileme yanıtı eski kullanıcının üstüne yazılır ve depoya konur. Protokolün kendisi etkileşim testlerinde,
// gerçek kitaplıkla sınanır (tests/e2e/provider.e2e.ts).

export type UserArgs = {
  access_token: string
  refresh_token?: string
  token_type: string
  profile: Record<string, unknown>
  id_token?: string
  userState?: unknown
}

export class User {
  readonly access_token: string
  readonly refresh_token: string | undefined
  readonly token_type: string
  readonly profile: Record<string, unknown>
  readonly id_token: string | undefined
  readonly state: unknown

  constructor(args: UserArgs) {
    this.access_token = args.access_token
    this.refresh_token = args.refresh_token
    this.token_type = args.token_type
    this.profile = args.profile
    this.id_token = args.id_token
    this.state = args.userState
  }
}

export class ErrorResponse extends Error {
  override readonly name = "ErrorResponse"
  readonly error: string | null

  constructor(args: { error: string }) {
    super(args.error)
    this.error = args.error
  }
}

export class ErrorTimeout extends Error {
  override readonly name = "ErrorTimeout"
}

export class InMemoryWebStorage {}

export class WebStorageStateStore {
  readonly store: unknown

  constructor(options: { store: unknown }) {
    this.store = options.store
  }
}

export type Settings = Record<string, unknown> & {
  authority: string
  client_id: string
  silent_redirect_uri?: string
}

/** Token ucunun yenileme yanıtı: alanları eski kullanıcının üstüne yazılır. */
export type Renewed = { access_token: string; refresh_token?: string }

type Behaviour = {
  /** `signinSilent`'ın sağlayıcıya sorduğu an; depodaki kullanıcıyla çağrılır. */
  renew: (user: User, manager: UserManager) => Renewed | Promise<Renewed>
  /** `signinRedirectCallback`'in sonucu. */
  callback: (url: string, manager: UserManager) => User | Promise<User>
  /** `signinRedirect`; gerçeği sayfadan ayrılır ve sonuçlanmaz. */
  redirect: (args: unknown, manager: UserManager) => void | Promise<void>
}

const unexpected = (what: string) => () => {
  throw new Error(`Unexpected ${what}.`)
}

export const provider = {
  /** Kurulan yöneticiler, kuruluş sırasıyla. */
  managers: [] as UserManager[],
  /** Yenileme için sağlayıcıya sunulan kullanıcılar. */
  renewals: [] as User[],
  /** `signinSilent` çağrılarının argümanları. */
  silentArgs: [] as unknown[],
  /** İşlenen dönüş adresleri. */
  callbacks: [] as string[],
  /** Başlatılan yönlendirmelerin argümanları. */
  redirects: [] as unknown[],
  /** `removeUser` çağrılarının sayısı. */
  removals: 0,
  behaviour: {
    renew: unexpected("renewal"),
    callback: unexpected("callback"),
    redirect: unexpected("redirect"),
  } as Behaviour,

  /** Kayıtları temizler ve sonraki çağrıların davranışını belirler. */
  respondWith(next: Partial<Behaviour> = {}) {
    this.renewals.length = 0
    this.silentArgs.length = 0
    this.callbacks.length = 0
    this.redirects.length = 0
    this.removals = 0
    this.behaviour = {
      renew: unexpected("renewal"),
      callback: unexpected("callback"),
      redirect: unexpected("redirect"),
      ...next,
    }
  },

  /** Bir yöneticinin deposunda kalmış kullanıcılar; işlem bitince boş olmalıdır. */
  storedUsers(): User[] {
    return this.managers.flatMap((manager) =>
      manager.stored ? [manager.stored] : []
    )
  },
}

export class UserManager {
  readonly settings: Settings
  stored: User | null = null

  constructor(settings: Settings) {
    this.settings = settings
    provider.managers.push(this)
  }

  async storeUser(user: User | null): Promise<void> {
    this.stored = user
  }

  async removeUser(): Promise<void> {
    provider.removals++
    this.stored = null
  }

  async signinRedirect(args: unknown): Promise<void> {
    provider.redirects.push(args)
    await provider.behaviour.redirect(args, this)
  }

  async signinRedirectCallback(url: string): Promise<User> {
    provider.callbacks.push(url)

    const user = await provider.behaviour.callback(url, this)
    this.stored = user
    return user
  }

  async signinSilent(args?: unknown): Promise<User> {
    const user = this.stored

    provider.silentArgs.push(args)

    if (!user?.refresh_token) {
      // Gerçeği burada gizli bir iframe açar; adresi boş bırakılmışsa bu hatayı verir.
      throw new Error(
        this.settings.silent_redirect_uri === ""
          ? "No silent_redirect_uri configured"
          : "The fake provider does not open an iframe."
      )
    }

    provider.renewals.push(user)

    const renewed = await provider.behaviour.renew(user, this)
    const next = new User({
      ...user,
      userState: user.state,
      ...renewed,
    })

    this.stored = next
    return next
  }
}
