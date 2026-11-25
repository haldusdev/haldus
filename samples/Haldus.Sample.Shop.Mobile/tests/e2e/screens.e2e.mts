import assert from 'node:assert/strict';

import { button, containing, field, role, text, type Locator } from './support/chrome.mts';
import { en, KEYS, scene, tr } from './support/harness.mts';

/** Alt çubuktaki sekme; sepetin sekmesi adının yanında satır sayısını da taşır. */
function tab(name: string): Locator {
  return {
    expression: `__probe.visible("[role=tab]").find((element) => __probe.nameOf(element).includes(${JSON.stringify(name)})) ?? null`,
    description: `tab "${name}"`,
  };
}

scene('the product list searches after typing stops', async ({ shop, page, signIn }) => {
  shop.addProducts(
    { name: 'Kettle', price: 42.5, description: 'Boils water.' },
    { name: 'Toaster' }
  );
  await signIn();

  await page.see(text('Kettle'));
  await page.see(text('Boils water.'));
  await page.see(text('Toaster'));
  const first = shop.last('GET', '/products');
  assert.equal(first.query.get('page'), '1');
  assert.equal(first.query.get('sort'), 'name');
  assert.equal(first.query.get('search'), null);

  // Her tuşta istek gitmez; yazma durunca tek istek gider.
  const before = shop.sent('GET', '/products').length;
  await page.fill(field(en.products.searchPlaceholder), 'to');
  await page.type('as');
  await page.type('ter');
  await page.gone(text('Kettle'));
  await page.see(text('Toaster'));
  assert.equal(shop.sent('GET', '/products').length, before + 1);
  assert.equal(shop.last('GET', '/products').query.get('search'), 'toaster');

  await page.fill(field(en.products.searchPlaceholder), 'nothing like this');
  await page.see(text(en.products.emptySearch));
});

scene('a long list loads the next page when the end is reached', async ({ shop, page, signIn }) => {
  shop.addProducts(
    ...Array.from({ length: 25 }, (_, index) => ({
      name: `Item ${String(index + 1).padStart(2, '0')}`,
    }))
  );
  await signIn();
  await page.see(text('Item 01'));

  // Listenin sonuna kaydırılır.
  await page.evaluate(`(() => {
    let node = __probe.text("Item 01");
    while (node && node.scrollHeight <= node.clientHeight + 1) node = node.parentElement;
    node.scrollTop = node.scrollHeight;
  })()`);

  await page.see(text('Item 25'));
  assert.equal(shop.last('GET', '/products').query.get('page'), '2');
  assert.equal(shop.last('GET', '/products').query.get('pageSize'), '20');
});

scene(
  'a favorite is added from the list and removed from the favorites tab',
  async ({ shop, page, signIn, fill }) => {
    shop.addProducts({ name: 'Kettle' }, { name: 'Toaster' });
    const kettle = shop.products[0];
    await signIn();

    await page.click(button(fill(en.products.addFavorite, { name: 'Kettle' })));

    // Düğmenin adı durumu söyler: artık favoridir.
    await page.see(button(fill(en.products.removeFavorite, { name: 'Kettle' })));
    assert.equal(shop.sent('PUT', `/me/favorites/${kettle.id}`).length, 1);
    assert.deepEqual([...(shop.favorites.get('account-1') ?? [])], [kettle.id]);

    await page.click(tab(en.tabs.favorites));
    await page.waitForPath('/favorites');
    await page.see(text('Kettle'));
    assert.equal(await page.exists(text('Toaster')), false);

    await page.click(button(fill(en.products.removeFavorite, { name: 'Kettle' })));
    await page.see(text(en.favorites.empty));
    assert.equal(shop.sent('DELETE', `/me/favorites/${kettle.id}`).length, 1);
  }
);

scene(
  'the basket is created on the first add and edited as a whole',
  async ({ shop, page, signIn, fill }) => {
    shop.addProducts({ name: 'Kettle' }, { name: 'Toaster' });
    await signIn();
    await page.see(text('Kettle'));

    // İlk ürün sepeti oluşturur.
    await page.click(button(fill(en.products.addToBasketLabel, { name: 'Kettle' })));
    await page.see(tab(`${en.tabs.basket}`));
    await page.waitFor(
      `__probe.visible("[role=tab]").some((element) => /1/.test(element.textContent))`,
      'the basket tab to show one line'
    );
    assert.deepEqual(shop.last('POST', '/me/baskets').body, {
      basketItems: [{ productName: 'Kettle', quantity: 1 }],
    });

    // Aynı ürün yeniden eklenince adedi artar; istek satırların tamamını kimlikleriyle taşır.
    const basket = shop.baskets.get('account-1');
    assert.ok(basket);
    const lineId = basket.basketItems[0].id;
    await page.click(button(fill(en.products.addToBasketLabel, { name: 'Kettle' })));
    await page.click(button(fill(en.products.addToBasketLabel, { name: 'Toaster' })));

    await page.click(tab(en.tabs.basket));
    await page.waitForPath('/basket');
    await page.see(text(fill(en.basket.quantity, { count: 2 })));
    await page.see(containing(fill(en.basket.lines_other, { count: 2 })));

    const updates = shop.sent('PUT', `/me/baskets/${basket.id}`);
    assert.deepEqual(updates[0].body, {
      note: null,
      basketItems: [{ id: lineId, productName: 'Kettle', quantity: 2 }],
    });

    // Adet artırılır ve azaltılır; bire inen satırın düğmesi "çıkar" olur.
    await page.click(button(fill(en.basket.increase, { name: 'Toaster' })));
    await page.see(button(fill(en.basket.decrease, { name: 'Toaster' })));
    await page.click(button(fill(en.basket.decrease, { name: 'Toaster' })));
    await page.see(button(fill(en.basket.remove, { name: 'Toaster' })));
    await page.click(button(fill(en.basket.remove, { name: 'Toaster' })));
    await page.gone(text('Toaster'));
    await page.see(containing(fill(en.basket.lines_one, { count: 1 })));

    await page.click(button(en.basket.clear));
    await page.see(text(en.basket.empty));
    assert.deepEqual(shop.last('PUT', `/me/baskets/${basket.id}`).body, {
      note: null,
      basketItems: [],
    });
  }
);

scene('a failed load offers a retry', async ({ shop, page, signIn }) => {
  shop.addProducts({ name: 'Kettle' });
  shop.failing = /^\/products/;
  await signIn();

  await page.see(text(en.common.loadFailed));

  shop.failing = undefined;
  await page.click(button(en.common.retry));
  await page.see(text('Kettle'));
});

scene(
  'the account screen verifies the e-mail address with the mailed code',
  async ({ shop, page, signIn, fill }) => {
    await signIn('grace@example.com', { verified: false, path: '/account' });
    await page.see(text(en.verify.title));
    await page.see(text(fill(en.verify.description, { email: 'grace@example.com' })));
    const before = await page.storage(KEYS.accessToken);

    await page.fill(field(en.verify.code), 'WRONGCOD');
    await page.click(button(en.verify.submit));
    assert.equal(await page.textOf(role('alert')), 'The code is invalid or has expired.');

    await page.click(button(en.verify.resend));
    await page.see(text(en.verify.resent));
    assert.equal(shop.sent('POST', '/auth/email/resend').length, 1);

    await page.fill(field(en.verify.code), 'k7m2-p9qx');
    await page.click(button(en.verify.submit));

    await page.gone(text(en.verify.title));
    // Doğrulama yeni token'lar verir; oturum onlarla güncellenir.
    assert.notEqual(await page.storage(KEYS.accessToken), before);
    assert.equal(
      (JSON.parse((await page.storage(KEYS.account)) ?? '{}') as { emailVerified: boolean })
        .emailVerified,
      true
    );
  }
);

scene(
  'switching the language translates the screens and the requests',
  async ({ shop, page, signIn }) => {
    await signIn('ada@example.com', { path: '/account' });
    await page.see(role('radio', en.languages.tr));
    assert.equal(await page.attribute(role('radio', en.languages.en), 'aria-checked'), 'true');
    assert.equal(await page.attribute(role('radio', en.languages.tr), 'aria-checked'), 'false');

    await page.click(role('radio', en.languages.tr));

    await page.see(button(tr.account.signOut));
    await page.see(tab(tr.tabs.basket));
    assert.equal(await page.attribute(role('radio', en.languages.tr), 'aria-checked'), 'true');

    // Dil backend'e de gider: önbellekteki yanıtlar yeni dille yeniden istenir.
    await page.click(tab(tr.tabs.products));
    await page.see(text(tr.products.empty));
    assert.equal(shop.last('GET', '/products').headers['accept-language'], 'tr');

    // Seçim saklanır; uygulama yeniden açılınca Türkçe başlar.
    await page.goto(`${shop.origin}/account`);
    await page.see(button(tr.account.signOut));
  }
);

scene('the password is changed from the account screen', async ({ shop, page, signIn }) => {
  await signIn('ada@example.com', { path: '/account' });

  await page.click(button(en.password.title));
  await page.fill(field(en.password.current), 'not-my-password');
  await page.fill(field(en.password.new), 'a-brand-new-password');
  await page.click(button(en.password.save));
  assert.equal(await page.textOf(role('alert')), 'The e-mail address or password is incorrect.');

  await page.fill(field(en.password.current), 'correct-horse-battery');
  await page.click(button(en.password.save));

  await page.see(text(en.password.changed));
  assert.equal(shop.accounts[0].password, 'a-brand-new-password');
});

scene(
  'signing out revokes the session and returns to the sign-in screen',
  async ({ shop, page, signIn }) => {
    await signIn('ada@example.com', { path: '/account' });

    await page.click(button(en.account.signOut));

    await page.waitForPath('/sign-in');
    assert.equal(shop.sent('POST', '/auth/logout').length, 1);
    assert.equal(await page.storage(KEYS.accessToken), null);
    assert.equal(await page.storage(KEYS.refreshToken), null);
    assert.equal(await page.storage(KEYS.account), null);
  }
);

scene('an expired access token is renewed once', async ({ shop, page, signIn }) => {
  shop.addProducts({ name: 'Kettle' });
  await signIn();
  await page.see(text('Kettle'));
  const before = await page.storage(KEYS.refreshToken);

  shop.expireAccessTokens();
  // Ekran yeniden açılınca ürünler, favoriler ve sepet aynı anda istenir; hepsi 401 alır.
  await page.goto(`${shop.origin}/`);
  await page.see(text('Kettle'));

  assert.equal(shop.sent('POST', '/auth/refresh').length, 1);
  assert.deepEqual(shop.last('POST', '/auth/refresh').body, { refreshToken: before });
  const accessToken = await page.storage(KEYS.accessToken);
  assert.equal(shop.last('GET', '/products').headers.authorization, `Bearer ${accessToken}`);
  assert.equal(shop.last('GET', '/me/baskets').headers.authorization, `Bearer ${accessToken}`);
});

scene('a rejected renewal returns to the sign-in screen', async ({ shop, page, signIn }) => {
  await signIn();
  await page.see(text(en.products.empty));

  shop.revokeSessions();
  // Sekmeler önbellekteki veriyi gösterir; istek gönderen bir işlem gerekir.
  await page.goto(`${shop.origin}/favorites`);

  await page.waitForPath('/sign-in');
  await page.see(field(en.signIn.email));
  assert.equal(await page.storage(KEYS.accessToken), null);
});
