import assert from 'node:assert/strict';

import { button, field, role, text } from './support/chrome.mts';
import { en, KEYS, scene, tr, until } from './support/harness.mts';

// Kimlik sağlayıcıyla giriş. Sağlayıcı, sahte mağazanın `/idp` adresindeki taklididir (support/shop.mts): uygulamanın
// gözünde ayrı bir sunucudur, ama aynı kaynakta durduğu için tarayıcının kaynaklar arası kuralları devreye girmez.
//
// Web hedefinde sağlayıcı bir açılır pencerede açılır. Sürücü o pencereyi sürmez; taklidin yetkilendirme sayfası
// kullanıcıya bir şey sormadan uygulamaya geri döner, pencereyi uygulamanın kendisi kapatır. Cihazdaki akış (sistem
// tarayıcısı, uygulamanın şemasına dönüş) bu testlerde yoktur.

type StoredAccount = {
  accountId: string;
  email: string;
  kind?: string;
  provider?: { clientId: string; tokenEndpoint: string };
};

function parse(value: string | null): StoredAccount {
  return JSON.parse(value ?? '{}') as StoredAccount;
}

scene('the sign-in screen offers no provider unless the backend does', async ({ shop, page }) => {
  await page.goto(`${shop.origin}/sign-in`);
  await page.see(field(en.signIn.email));

  // Yapılandırma okunduktan sonra da düğme yoktur.
  await until(() => shop.sent('GET', '/auth/config').length > 0, 'the configuration to be read');
  await page.evaluate('new Promise((resolve) => setTimeout(resolve, 300))');

  assert.equal(await page.exists(button(en.signIn.provider)), false);
  assert.equal(await page.exists(text(en.signIn.or)), false);
  // Yapılandırma isteği oturum istemez ve token taşımaz.
  assert.equal(shop.last('GET', '/auth/config').headers.authorization, undefined);
  assert.equal(shop.sent('GET', '/idp/.well-known/openid-configuration').length, 0);
});

scene('the provider button appears when the backend offers a provider', async ({ shop, page }) => {
  shop.offerProvider();
  await page.goto(`${shop.origin}/sign-in`);

  await page.see(button(en.signIn.provider));
  await page.see(text(en.signIn.or));
  // Parola formu yerindedir.
  await page.see(field(en.signIn.email));
  await page.see(field(en.signIn.password));
  await page.see(button(en.signIn.submit));

  // Düğme yalnızca giriş kipindedir; kayıt ve parola sıfırlama parola hesabına aittir.
  await page.click(button(en.signIn.switchToRegister));
  await page.see(text(en.signIn.registerDescription));
  assert.equal(await page.exists(button(en.signIn.provider)), false);
});

scene('the provider button and its messages are translated', async ({ shop, page }) => {
  shop.offerProvider({ answer: 'deny' });
  // Saklanan dil seçimi; uygulama onunla açılır.
  await page.goto(`${shop.origin}/sign-in`);
  await page.setStorage('haldus-shop.language', 'tr');
  await page.goto(`${shop.origin}/sign-in`);

  await page.see(text(tr.signIn.or));
  await page.click(button(tr.signIn.provider));

  assert.equal(await page.textOf(role('alert')), tr.signIn.providerFailed);
});

scene('password sign-in still works when a provider is offered', async ({ shop, page }) => {
  const account = shop.addAccount('ada@example.com', 'correct-horse-battery');
  shop.addProducts({ name: 'Kettle' });
  shop.offerProvider();
  await page.goto(`${shop.origin}/sign-in`);
  await page.see(button(en.signIn.provider));

  await page.fill(field(en.signIn.email), 'ada@example.com');
  await page.fill(field(en.signIn.password), 'correct-horse-battery');
  await page.click(button(en.signIn.submit));

  await page.see(text('Kettle'));
  assert.match((await page.storage(KEYS.accessToken)) ?? '', /^access-/);
  assert.match((await page.storage(KEYS.refreshToken)) ?? '', /^refresh-/);
  // Parola oturumunun kaydı tür taşımaz (önceki sürümün yazdığı biçim).
  assert.deepEqual(JSON.parse((await page.storage(KEYS.account)) ?? '{}'), {
    accountId: account.id,
    email: 'ada@example.com',
    tenantId: account.tenantId,
    emailVerified: true,
  });
  assert.equal(shop.sent('GET', '/idp/authorize').length, 0);
  assert.equal(shop.sent('POST', '/idp/token').length, 0);
});

scene(
  'the password form still works when the configuration cannot be read',
  async ({ shop, page }) => {
    shop.addAccount('ada@example.com', 'correct-horse-battery');
    shop.addProducts({ name: 'Kettle' });
    shop.offerProvider();
    shop.failing = /^\/auth\/config/;
    await page.goto(`${shop.origin}/sign-in`);

    await page.fill(field(en.signIn.email), 'ada@example.com');
    await page.fill(field(en.signIn.password), 'correct-horse-battery');
    await page.click(button(en.signIn.submit));

    await page.see(text('Kettle'));
    assert.ok(shop.sent('GET', '/auth/config').length > 0);
  }
);

scene(
  'only the provider is offered when the backend turns password sign-in off',
  async ({ shop, page }) => {
    shop.offerProvider();
    shop.passwordSignIn = false;
    await page.goto(`${shop.origin}/sign-in`);

    await page.see(button(en.signIn.provider));
    await page.see(text(en.signIn.providerDescription));
    await page.gone(field(en.signIn.email));
    assert.equal(await page.exists(field(en.signIn.password)), false);
    assert.equal(await page.exists(button(en.signIn.switchToRegister)), false);
    assert.equal(await page.exists(button(en.signIn.forgot)), false);
    assert.equal(await page.exists(text(en.signIn.or)), false);
  }
);

scene(
  'signing in with the provider exchanges the code and opens the session',
  async ({ shop, page }) => {
    const provider = shop.offerProvider({ user: 'grace@example.com' });
    shop.addProducts({ name: 'Kettle' });
    await page.goto(`${shop.origin}/sign-in`);

    await page.click(button(en.signIn.provider));

    await page.see(text('Kettle'));
    assert.equal(await page.path(), '/');

    // Yetkilendirme isteği: kod akışı, PKCE (S256), istemci, kapsamlar ve giriş ekranına dönüş.
    const authorize = shop.last('GET', '/idp/authorize').query;
    assert.equal(authorize.get('response_type'), 'code');
    assert.equal(authorize.get('client_id'), provider.clientId);
    assert.equal(authorize.get('scope'), provider.scopes);
    assert.equal(authorize.get('redirect_uri'), `${shop.origin}/sign-in`);
    assert.equal(authorize.get('code_challenge_method'), 'S256');
    assert.match(authorize.get('code_challenge') ?? '', /^[\w-]{43}$/);
    assert.ok(authorize.get('state'));
    assert.equal(authorize.get('client_secret'), null);

    // Kod bir kez, doğrulayıcısıyla ve gizli anahtar olmadan token'a çevrilir.
    const exchanges = shop.sent('POST', '/idp/token');
    assert.equal(exchanges.length, 1);
    const exchange = exchanges[0].body as Record<string, string>;
    assert.equal(exchange.grant_type, 'authorization_code');
    assert.equal(exchange.client_id, provider.clientId);
    assert.equal(exchange.redirect_uri, `${shop.origin}/sign-in`);
    assert.match(exchange.code, /^code-\d+$/);
    assert.match(exchange.code_verifier, /^[\w.~-]{43,128}$/);
    assert.equal(exchange.client_secret, undefined);
    assert.equal(exchanges[0].headers.authorization, undefined);

    // Backend'e hesabın kim olduğu sağlayıcının erişim token'ı ile sorulur; backend'in giriş uçları kullanılmaz.
    const accessToken = await page.storage(KEYS.accessToken);
    assert.match(accessToken ?? '', /^idp-access-\d+$/);
    assert.equal(shop.sent('GET', '/auth/me').length, 1);
    assert.equal(shop.last('GET', '/auth/me').headers.authorization, `Bearer ${accessToken}`);
    assert.equal(shop.sent('POST', '/auth/login').length, 0);
    assert.equal(shop.sent('POST', '/auth/refresh').length, 0);

    // Oturum türüyle ve yenilenmesi için gerekenlerle saklanır.
    const account = shop.accounts.find((candidate) => candidate.email === 'grace@example.com');
    assert.ok(account);
    assert.match((await page.storage(KEYS.refreshToken)) ?? '', /^idp-refresh-\d+$/);
    assert.deepEqual(JSON.parse((await page.storage(KEYS.account)) ?? '{}'), {
      accountId: account.id,
      email: 'grace@example.com',
      tenantId: account.tenantId,
      emailVerified: true,
      kind: 'provider',
      provider: { clientId: provider.clientId, tokenEndpoint: `${shop.authority}/token` },
    });
    assert.equal(shop.last('GET', '/products').headers.authorization, `Bearer ${accessToken}`);

    // Uygulama yeniden açılınca oturum depodan geri gelir.
    await page.goto(`${shop.origin}/account`);
    await page.see(text('grace@example.com'));
  }
);

scene('a provider sign-in that is closed can be started again', async ({ shop, page }) => {
  const provider = shop.offerProvider({ answer: 'close' });
  shop.addProducts({ name: 'Kettle' });
  await page.goto(`${shop.origin}/sign-in`);

  // Kullanıcı sağlayıcının penceresini giriş yapmadan kapatır.
  await page.click(button(en.signIn.provider));

  assert.equal(await page.textOf(role('status')), en.signIn.providerCancelled);
  assert.equal(await page.path(), '/sign-in');
  assert.equal(await page.storage(KEYS.accessToken), null);
  assert.equal(shop.sent('POST', '/idp/token').length, 0);
  assert.equal(shop.sent('GET', '/auth/me').length, 0);

  // Sıradaki deneme yeni bir istekle (yeni durum ve PKCE değerleriyle) yapılır.
  provider.answer = 'approve';
  await page.click(button(en.signIn.provider));

  await page.see(text('Kettle'));
  const [first, second] = shop.sent('GET', '/idp/authorize').map((request) => request.query);
  assert.notEqual(first.get('state'), second.get('state'));
  assert.notEqual(first.get('code_challenge'), second.get('code_challenge'));
});

scene('a provider that refuses the sign-in shows a message', async ({ shop, page }) => {
  shop.offerProvider({ answer: 'deny' });
  await page.goto(`${shop.origin}/sign-in`);

  await page.click(button(en.signIn.provider));

  assert.equal(await page.textOf(role('alert')), en.signIn.providerFailed);
  assert.equal(await page.path(), '/sign-in');
  assert.equal(await page.storage(KEYS.accessToken), null);
  assert.equal(shop.sent('POST', '/idp/token').length, 0);

  // Parola formuyla giriş denemesi sağlayıcının mesajını kaldırır; yerine o denemenin sonucu gelir.
  await page.fill(field(en.signIn.email), 'nobody@example.com');
  await page.fill(field(en.signIn.password), 'wrong-password');
  await page.click(button(en.signIn.submit));
  await page.see(text('The e-mail address or password is incorrect.'));
  assert.equal(await page.exists(text(en.signIn.providerFailed)), false);
});

scene('a token the backend does not accept opens no session', async ({ shop, page }) => {
  // Sağlayıcı kullanıcıyı tanır ve token verir, ama backend o token'ı reddeder (401).
  shop.offerProvider({ accepted: false });
  await page.goto(`${shop.origin}/sign-in`);

  await page.click(button(en.signIn.provider));

  assert.equal(await page.textOf(role('alert')), en.signIn.providerFailed);
  assert.equal(await page.path(), '/sign-in');
  assert.equal(await page.storage(KEYS.accessToken), null);
  assert.equal(await page.storage(KEYS.refreshToken), null);
  assert.equal(await page.storage(KEYS.account), null);
  // Reddedilen token için sağlayıcıdan yenisi istenmez: tek token isteği kodun çevrilmesidir.
  assert.equal(shop.sent('POST', '/idp/token').length, 1);
  assert.equal(shop.sent('GET', '/auth/me').length, 1);
});

scene('a failing backend during the provider sign-in shows its message', async ({ shop, page }) => {
  shop.offerProvider();
  shop.failing = /^\/auth\/me/;
  await page.goto(`${shop.origin}/sign-in`);

  await page.click(button(en.signIn.provider));

  // Backend bir gerekçe yazdıysa o gösterilir.
  assert.equal(await page.textOf(role('alert')), 'Internal server error');
  assert.equal(await page.storage(KEYS.accessToken), null);
});

scene(
  'a provider that cannot be reached is reported and can be retried',
  async ({ shop, page }) => {
    shop.offerProvider();
    shop.addProducts({ name: 'Kettle' });
    shop.failing = /^\/idp\/\.well-known/;
    await page.goto(`${shop.origin}/sign-in`);

    assert.equal(await page.textOf(role('alert')), en.signIn.providerUnavailable);
    // Parola formu bu sırada kullanılabilir.
    await page.see(button(en.signIn.submit));

    // Düğme keşfi yeniden dener; başarılı olunca giriş başlatılabilir.
    shop.failing = undefined;
    await page.click(button(en.signIn.provider));
    await page.gone(role('alert'));
    await page.click(button(en.signIn.provider));

    await page.see(text('Kettle'));
  }
);

scene(
  'an expired provider token is renewed at the provider, not at the backend',
  async ({ shop, page, signInAtProvider }) => {
    const provider = shop.offerProvider();
    shop.addProducts({ name: 'Kettle' });
    await signInAtProvider();
    await page.see(text('Kettle'));
    const before = await page.storage(KEYS.refreshToken);
    const account = await page.storage(KEYS.account);

    shop.expireAccessTokens();
    // Ekran yeniden açılınca ürünler, favoriler ve sepet aynı anda istenir; hepsi 401 alır.
    await page.goto(`${shop.origin}/`);
    await page.see(text('Kettle'));

    const renewals = shop.sent('POST', '/idp/token');
    assert.equal(renewals.length, 1);
    assert.deepEqual(renewals[0].body, {
      grant_type: 'refresh_token',
      client_id: provider.clientId,
      refresh_token: before,
    });
    assert.equal(renewals[0].headers.authorization, undefined);
    assert.equal(shop.sent('POST', '/auth/refresh').length, 0);

    const accessToken = await page.storage(KEYS.accessToken);
    assert.match(accessToken ?? '', /^idp-access-\d+$/);
    assert.notEqual(await page.storage(KEYS.refreshToken), before);
    // Hesap kaydı (tür ve sağlayıcı) yenilemeyle değişmez.
    assert.equal(await page.storage(KEYS.account), account);
    assert.equal(shop.last('GET', '/products').headers.authorization, `Bearer ${accessToken}`);
    assert.equal(shop.last('GET', '/me/baskets').headers.authorization, `Bearer ${accessToken}`);
  }
);

scene(
  'a renewal the provider rejects returns to the sign-in screen',
  async ({ shop, page, signInAtProvider }) => {
    shop.offerProvider();
    await signInAtProvider();
    await page.see(text(en.products.empty));

    shop.revokeProviderGrants();
    await page.goto(`${shop.origin}/favorites`);

    await page.waitForPath('/sign-in');
    await page.see(button(en.signIn.provider));
    assert.ok(shop.sent('POST', '/idp/token').length > 0);
    assert.equal(shop.sent('POST', '/auth/refresh').length, 0);
    assert.equal(shop.sent('POST', '/auth/logout').length, 0);
    assert.equal(await page.storage(KEYS.accessToken), null);
    assert.equal(await page.storage(KEYS.refreshToken), null);
    assert.equal(await page.storage(KEYS.account), null);
  }
);

scene(
  'a provider outage during renewal keeps the session',
  async ({ shop, page, signInAtProvider }) => {
    shop.offerProvider();
    shop.addProducts({ name: 'Kettle' });
    await signInAtProvider();
    await page.see(text('Kettle'));
    const before = await page.storage(KEYS.refreshToken);

    shop.expireAccessTokens();
    shop.failing = /^\/idp\/token/;
    await page.goto(`${shop.origin}/`);

    // İstekler başarısız olur ama kullanıcı oturumdan atılmaz.
    await page.see(text(en.common.loadFailed));
    assert.equal(await page.path(), '/');
    assert.equal(await page.storage(KEYS.refreshToken), before);

    // Sağlayıcı düzelince aynı yenileme token'ı ile oturum sürer.
    shop.failing = undefined;
    await page.click(button(en.common.retry));
    await page.see(text('Kettle'));
    assert.notEqual(await page.storage(KEYS.refreshToken), before);
  }
);

scene(
  'a provider session without a refresh token ends when the token is rejected',
  async ({ shop, page, signInAtProvider }) => {
    shop.offerProvider({ refreshTokens: false });
    shop.addProducts({ name: 'Kettle' });
    await signInAtProvider('ada@example.com', { refreshToken: false });
    await page.see(text('Kettle'));
    assert.equal(await page.storage(KEYS.refreshToken), null);

    shop.expireAccessTokens();
    await page.goto(`${shop.origin}/favorites`);

    await page.waitForPath('/sign-in');
    assert.equal(shop.sent('POST', '/idp/token').length, 0);
    assert.equal(shop.sent('POST', '/auth/refresh').length, 0);
    assert.equal(await page.storage(KEYS.accessToken), null);
    assert.equal(await page.storage(KEYS.account), null);
  }
);

scene(
  'the account screen hides what belongs to password accounts',
  async ({ shop, page, signInAtProvider }) => {
    shop.offerProvider();
    // Backend'de e-postası doğrulanmamış görünen bir hesap: parola hesabı olsaydı kod istenirdi.
    await signInAtProvider('grace@example.com', { verified: false, path: '/account' });

    await page.see(text('grace@example.com'));
    await page.see(button(en.account.signOut));
    assert.equal(await page.exists(text(en.verify.title)), false);
    assert.equal(await page.exists(button(en.password.title)), false);
    assert.equal(parse(await page.storage(KEYS.account)).kind, 'provider');
  }
);

scene(
  'signing out of a provider session clears the device and calls nobody',
  async ({ shop, page, signInAtProvider }) => {
    shop.offerProvider();
    await signInAtProvider('ada@example.com', { path: '/account' });
    await page.see(button(en.account.signOut));
    const before = shop.requests.length;

    await page.click(button(en.account.signOut));

    await page.waitForPath('/sign-in');
    await page.see(button(en.signIn.provider));
    assert.equal(await page.storage(KEYS.accessToken), null);
    assert.equal(await page.storage(KEYS.refreshToken), null);
    assert.equal(await page.storage(KEYS.account), null);
    // Backend'de kapatılacak bir oturum yoktur; sağlayıcının oturumu da kapatılmaz (bilinen sınır).
    assert.equal(shop.sent('POST', '/auth/logout').length, 0);
    assert.deepEqual(
      shop.requests.slice(before).filter((request) => request.path.startsWith('/idp/token')),
      []
    );
  }
);
